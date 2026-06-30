using System.Net;
using System.Net.Http.Json;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.Realms;
using Modgud.Authorization.Apps;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1c: the control-plane provisioning endpoints exposing the RealmManifestApplier
/// over HTTP — POST /import (new realm), POST /{slug}/apply (in-place update), and
/// DELETE /{slug}?hard=true (drop the tenant DB). Drives them as the control-plane admin
/// against an isolated cold-boot host so the real tenant-DB create/drop pollutes nothing.
/// </summary>
public class RealmProvisioningEndpointsTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Import_then_apply_then_hard_delete_round_trip()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();
        var svc = factory.Services.GetRequiredService<IRealmProvisioningService>();

        const string slug = "initech";

        // ── Import ────────────────────────────────────────────────────────────
        var importResp = await client.PostAsJsonAsync(
            "/api/admin/realms/import", BuildManifest(slug, "Initech App"), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Created, importResp.StatusCode);

        var imported = await importResp.Content.ReadFromJsonAsync<RealmImportResult>(factory.JsonOptions, ct);
        Assert.NotNull(imported);
        Assert.Equal(slug, imported!.Slug);
        Assert.True(imported.ClientSecrets.ContainsKey("initech-web"));
        Assert.NotNull(await svc.GetRealmBySlugAsync(slug, ct));

        // ── Apply (in-place update: change the app display name) ───────────────
        var applyResp = await client.PostAsJsonAsync(
            $"/api/admin/realms/{slug}/apply", BuildManifest(slug, "Initech App v2"), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var app = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "initech-app", ct);
            Assert.Equal("Initech App v2", app.DisplayName);
        });

        // ── Hard delete (drops the tenant DB) ─────────────────────────────────
        var deleteResp = await client.DeleteAsync($"/api/admin/realms/{slug}?hard=true", ct);
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);
        Assert.Null(await svc.GetRealmBySlugAsync(slug, ct));
    }

    [Fact]
    public async Task Import_rejects_duplicate_slug_with_409()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        const string slug = "dup-ep";
        var first = await client.PostAsJsonAsync(
            "/api/admin/realms/import", BuildManifest(slug, "Dup"), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/api/admin/realms/import", BuildManifest(slug, "Dup"), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("Realm.AlreadyExists", await second.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Apply_to_missing_realm_returns_404()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/admin/realms/ghost-ep/apply", BuildManifest("ghost-ep", "Ghost"), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Contains("Realm.NotFound", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Apply_with_route_slug_not_matching_manifest_returns_400()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/admin/realms/other-slug/apply", BuildManifest("manifest-slug", "X"), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Manifest.SlugMismatch", await resp.Content.ReadAsStringAsync(ct));
    }

    private static RealmManifest BuildManifest(string slug, string appDisplayName) => new()
    {
        Realm = new CreateRealmDto
        {
            Slug = slug,
            DisplayName = slug,
            Domains = [$"{slug}.localhost"],
            InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
        },
        Apps =
        [
            new RealmManifestApp
            {
                Slug = "initech-app",
                DisplayName = appDisplayName,
                Permissions = [new RealmManifestPermission("initech", "read")],
            },
        ],
        Clients =
        [
            new RealmManifestClient
            {
                ClientId = "initech-web",
                DisplayName = "Initech Web",
                ClientType = "confidential",
                RedirectUris = [$"https://{slug}.test/cb"],
                Scopes = ["openid"],
                AllowedGrantTypes = ["authorization_code", "refresh_token"],
                Apps = ["initech-app"],
            },
        ],
        Users =
        [
            new RealmManifestUser { Key = "admin", Email = $"admin@{slug}.test", UserName = "admin", Password = "Passw0rd!23" },
        ],
    };

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
