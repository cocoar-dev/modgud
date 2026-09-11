using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Helper;
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
    public async Task Create_then_apply_then_hard_delete_round_trip()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();
        var svc = factory.Services.GetRequiredService<IRealmProvisioningService>();

        const string slug = "initech";

        // ── Import ────────────────────────────────────────────────────────────
        await CreateRealmAsync(client, slug, factory.JsonOptions, ct);
        Assert.NotNull(await svc.GetRealmBySlugAsync(slug, ct));

        var fillResp = await client.PostAsJsonAsync(
            $"/api/admin/realms/{slug}/apply", BuildManifest(slug, "Initech App"), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, fillResp.StatusCode);

        var imported = await fillResp.Content.ReadFromJsonAsync<RealmImportResult>(factory.JsonOptions, ct);
        Assert.NotNull(imported);
        Assert.Equal(slug, imported!.Slug);
        Assert.True(imported.ClientSecrets.ContainsKey("initech-web"));

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
        await CreateRealmAsync(client, slug, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/api/admin/realms/{slug}/apply", BuildManifest(slug, "Ex EP App"), factory.JsonOptions, ct)).StatusCode);

        var resp = await client.GetAsync($"/api/admin/realms/{slug}/export", ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        // No realm shell: an export is content, not identity — that is what lets the same
        // file be applied to a different realm than the one it came from.
        Assert.False(root.TryGetProperty("Realm", out _), "the export must not carry a realm shell");

        // The confidential client is present but its secret is omitted (structure-only).
        var web = root.GetProperty("Clients").EnumerateArray()
            .Single(c => c.GetProperty("ClientId").GetString() == "initech-web");
        Assert.False(web.TryGetProperty("ClientSecret", out var secret) && secret.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Creating_a_realm_twice_rejects_the_duplicate_slug_with_409()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        const string slug = "dup-ep";
        await CreateRealmAsync(client, slug, factory.JsonOptions, ct);

        var second = await client.PostAsJsonAsync("/api/admin/realms", RealmShell(slug), factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains(slug, await second.Content.ReadAsStringAsync(ct));
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

    /// <summary>
    /// The invariant that replaced the old slug-mismatch guard: a manifest names no realm, so
    /// ONE file applies to any number of realms and the route alone decides where it lands.
    /// There is nothing left to mismatch.
    /// </summary>
    [Fact]
    public async Task One_manifest_applies_unchanged_to_two_different_realms()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        const string one = "twinsone";
        const string two = "twinstwo";
        var manifest = BuildManifest(one, "Twins App");

        foreach (var slug in new[] { one, two })
        {
            await CreateRealmAsync(client, slug, factory.JsonOptions, ct);
            var resp = await client.PostAsJsonAsync(
                $"/api/admin/realms/{slug}/apply", manifest, factory.JsonOptions, ct);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        foreach (var slug in new[] { one, two })
        {
            await InTenantAsync(factory, slug, async sp =>
            {
                var session = sp.GetRequiredService<IDocumentSession>();
                var app = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "initech-app", ct);
                Assert.Equal("Twins App", app.DisplayName);
            });
        }
    }

    [Fact]
    public async Task Apply_with_prune_true_removes_a_client_absent_from_the_manifest()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        const string slug = "pruneep";
        await CreateRealmAsync(client, slug, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/api/admin/realms/{slug}/apply", BuildManifest(slug, "Prune EP"), factory.JsonOptions, ct)).StatusCode);

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
        foreach (var section in new[] { "Settings", "Apps", "Apis", "Scopes", "Clients", "Roles", "Users", "Groups" })
            Assert.True(props.TryGetProperty(section, out _), $"schema missing '{section}'");

        // A manifest is content only — no realm shell, and nothing is required: every section
        // defaults to empty, so a partial manifest is a first-class payload.
        Assert.False(props.TryGetProperty("Realm", out _), "the schema must not describe a realm shell");
        if (root.TryGetProperty("required", out var required))
            Assert.DoesNotContain("Realm", required.EnumerateArray().Select(e => e.GetString()));

        // Field-level [Description]s are injected (proves the docs ride along).
        Assert.Contains("permission namespace", props.GetProperty("Apps").GetProperty("description").GetString());
        Assert.Contains("resource:action", body); // RealmManifestPermission description

        // A worked example is attached so a consumer can author a manifest from the schema alone.
        var examples = root.GetProperty("examples");
        Assert.True(examples.GetArrayLength() >= 1);
        Assert.Equal("acme", examples[0].GetProperty("Apps")[0].GetProperty("Slug").GetString());
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

    private static CreateRealmDto RealmShell(string slug) => new()
    {
        Slug = slug,
        DisplayName = slug,
        Domains = [$"{slug}.localhost"],
    };

    /// <summary>Creates the realm shell — the first half of provisioning, now that a manifest
    /// carries no realm identity. The second half is a POST to <c>{slug}/apply</c>.</summary>
    private static async Task CreateRealmAsync(
        HttpClient client, string slug, JsonSerializerOptions json, CancellationToken ct)
    {
        var resp = await client.PostAsJsonAsync("/api/admin/realms", RealmShell(slug), json, ct);
        Assert.True(resp.IsSuccessStatusCode,
            $"creating realm '{slug}' failed with {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
    }

    // Stable pinned ids (ADR 0024): a second apply of this manifest means the SAME
    // entities, and only an id can say that. The same ids in two different realms are
    // fine — that is the stage → prod transfer these tests stand in for.
    private static readonly string AppId = new ShortGuid(Guid.NewGuid()).ToString();
    private static readonly string ClientId = new ShortGuid(Guid.NewGuid()).ToString();
    private static readonly string UserId = new ShortGuid(Guid.NewGuid()).ToString();

    private static RealmManifest BuildManifest(string slug, string appDisplayName) => new()
    {
        Apps =
        [
            new RealmManifestApp
            {
                Slug = "initech-app",
                Id = AppId,
                DisplayName = appDisplayName,
                Permissions = [new RealmManifestPermission("initech", "read")],
            },
        ],
        Clients =
        [
            new RealmManifestClient
            {
                ClientId = "initech-web",
                Id = ClientId,
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
            new RealmManifestUser { Key = "admin", Id = UserId, Email = $"admin@{slug}.test", UserName = "admin", Password = "Passw0rd!23" },
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
