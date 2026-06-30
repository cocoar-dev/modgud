using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task Export_endpoint_returns_a_structure_only_manifest()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        const string slug = "exportep";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            "/api/admin/realms/import", BuildManifest(slug, "Ex EP App"), factory.JsonOptions, ct)).StatusCode);

        var resp = await client.GetAsync($"/api/admin/realms/{slug}/export", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        Assert.Equal(slug, root.GetProperty("Realm").GetProperty("Slug").GetString());

        // The confidential client is present but its secret is omitted (structure-only).
        var web = root.GetProperty("Clients").EnumerateArray()
            .Single(c => c.GetProperty("ClientId").GetString() == "initech-web");
        Assert.False(web.TryGetProperty("ClientSecret", out var secret) && secret.ValueKind != JsonValueKind.Null);
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

    [Fact]
    public async Task Apply_with_prune_true_removes_a_client_absent_from_the_manifest()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        const string slug = "pruneep";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
            "/api/admin/realms/import", BuildManifest(slug, "Prune EP"), factory.JsonOptions, ct)).StatusCode);

        // Re-apply with ?prune=true a manifest that drops the client → it must be pruned.
        var withoutClient = BuildManifest(slug, "Prune EP") with { Clients = [] };
        var applyResp = await client.PostAsJsonAsync(
            $"/api/admin/realms/{slug}/apply?prune=true", withoutClient, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.False(
                await session.Query<Modgud.Domain.OAuth.Applications.OAuthApplicationState>()
                    .AnyAsync(x => !x.IsDeleted && x.ClientId == "initech-web", ct),
                "the client absent from the ?prune=true manifest was pruned");
            // The app is still in the manifest → untouched.
            Assert.True(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "initech-app", ct));
        });
    }

    [Fact]
    public async Task Manifest_schema_endpoint_returns_a_described_json_schema_with_an_example()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        var resp = await client.GetAsync("/api/admin/realms/manifest-schema", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        // A real JSON Schema for an object with all the manifest sections.
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.True(root.TryGetProperty("$schema", out _));
        var props = root.GetProperty("properties");
        foreach (var section in new[] { "Realm", "Settings", "Apps", "Apis", "Scopes", "Clients", "Roles", "Users", "Groups" })
            Assert.True(props.TryGetProperty(section, out _), $"schema missing '{section}'");

        // Only the realm shell is required; the entity lists default to empty.
        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Realm", required);
        Assert.DoesNotContain("Apps", required);

        // Field-level [Description]s are injected (proves the docs ride along).
        Assert.Contains("permission namespace", props.GetProperty("Apps").GetProperty("description").GetString());
        Assert.Contains("resource:action", body); // RealmManifestPermission description

        // A worked example is attached so a consumer can author a manifest from the schema alone.
        var examples = root.GetProperty("examples");
        Assert.True(examples.GetArrayLength() >= 1);
        Assert.Equal("acme-test", examples[0].GetProperty("Realm").GetProperty("Slug").GetString());
    }

    [Fact]
    public async Task Manifest_schema_endpoint_is_gated_for_an_unauthenticated_caller()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        // No login → the schema (gated with realm:write, same as import/apply) must not leak.
        var anon = host.Factory.CreateClient();
        var resp = await anon.GetAsync("/api/admin/realms/manifest-schema", ct);

        Assert.NotEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(resp.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
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
