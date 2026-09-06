using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// The per-realm (data-plane) declarative-config surface: a <c>realm:admin</c> manages THEIR
/// OWN realm from a manifest via <c>/api/admin/realm-config/*</c> — reusing the applier/exporter
/// but scoped to the host-routed realm and gated by realm:admin (not the control plane). It can
/// fully edit the realm's config + entities (incl. prune within the realm), but cannot target
/// another realm, and create/delete-realm stay control-plane-only.
/// </summary>
public class RealmConfigEndpointsTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Apply_manages_the_current_realm_for_a_realm_admin()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Slug omitted → the endpoint pins the manifest to the caller's current realm.
        var manifest = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "rc-app", DisplayName = "Realm-Config App",
                      Permissions = new[] { new { Resource = "rc", Action = "read" } } },
            },
        };

        var apply = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", manifest, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

        // It landed in the current (system) realm — the data-plane apply targets TenantContext.Current.
        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.True(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "rc-app", ct),
                "rc-app applied to the current realm via the data-plane endpoint");
        });

        // Export of the current realm works on the same surface (round-trips the manifest).
        var export = await client.GetAsync("/api/admin/realm-config/export", ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("rc-app", await export.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Apply_refuses_a_manifest_targeting_a_different_realm()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // A realm admin may only manage their own realm — a foreign slug is the data-plane boundary.
        var foreign = new
        {
            Realm = new { Slug = "some-other-realm" },
            Apps = new[] { new { Slug = "x", DisplayName = "X", Permissions = new object[0] } },
        };

        var resp = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", foreign, factory.JsonOptions, ct);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Manifest.SlugMismatch", await resp.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Plan_previews_create_update_and_unchanged_without_writing()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Seed one app through the real apply so the plan has an existing entity to diff.
        var seed = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "plan-app", DisplayName = "Plan App",
                      Permissions = new[] { new { Resource = "plan", Action = "read" } } },
            },
        };
        var seedResp = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", seed, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, seedResp.StatusCode);

        // Plan: rename the existing app + add a new one. Nothing may be written.
        var manifest = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "plan-app", DisplayName = "Plan App v2",
                      Permissions = new[] { new { Resource = "plan", Action = "read" } } },
                new { Slug = "plan-app-2", DisplayName = "Second App",
                      Permissions = new[] { new { Resource = "plan2", Action = "read" } } },
            },
        };
        var planResp = await client.PostAsJsonAsync(
            "/api/admin/realm-config/plan", manifest, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, planResp.StatusCode);

        var plan = JsonNode.Parse(await planResp.Content.ReadAsStringAsync(ct))!;
        var apps = plan["Sections"]!.AsArray()
            .Single(s => s!["Name"]!.GetValue<string>() == "apps")!["Entries"]!.AsArray();

        var updated = apps.Single(e => e!["Key"]!.GetValue<string>() == "plan-app")!;
        Assert.Equal("update", updated["Action"]!.GetValue<string>());
        var change = updated["Changes"]!.AsArray()
            .Single(c => c!["Field"]!.GetValue<string>() == "DisplayName")!;
        Assert.Equal("Plan App", change["Current"]!.GetValue<string>());
        Assert.Equal("Plan App v2", change["Desired"]!.GetValue<string>());

        var created = apps.Single(e => e!["Key"]!.GetValue<string>() == "plan-app-2")!;
        Assert.Equal("create", created["Action"]!.GetValue<string>());

        // Dry-run: the rename must NOT have been applied.
        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var app = await session.Query<App>().FirstAsync(a => !a.IsDeleted && a.Slug == "plan-app", ct);
            Assert.Equal("Plan App", app.DisplayName);
            Assert.False(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "plan-app-2", ct),
                "plan must not create entities");
        });

        // Re-planning the SEEDED manifest is a no-op: the entry comes back unchanged.
        var idempotent = await client.PostAsJsonAsync(
            "/api/admin/realm-config/plan", seed, factory.JsonOptions, ct);
        var idemPlan = JsonNode.Parse(await idempotent.Content.ReadAsStringAsync(ct))!;
        var idemEntry = idemPlan["Sections"]!.AsArray()
            .Single(s => s!["Name"]!.GetValue<string>() == "apps")!["Entries"]!.AsArray()
            .Single(e => e!["Key"]!.GetValue<string>() == "plan-app")!;
        Assert.Equal("unchanged", idemEntry["Action"]!.GetValue<string>());
    }

    [Fact]
    public async Task Plan_with_prune_lists_delete_candidates_and_lockout_protections()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        var seed = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "prune-app", DisplayName = "Prune Me",
                      Permissions = new[] { new { Resource = "pr", Action = "read" } } },
            },
        };
        var seedResp = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", seed, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, seedResp.StatusCode);

        // An empty manifest + prune: the seeded app is a delete candidate, while the
        // realm-admin user and role are marked protected — and nothing is written.
        var empty = new { Realm = new { } };
        var planResp = await client.PostAsJsonAsync(
            "/api/admin/realm-config/plan?prune=true", empty, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, planResp.StatusCode);

        var plan = JsonNode.Parse(await planResp.Content.ReadAsStringAsync(ct))!;
        Assert.True(plan["Prune"]!.GetValue<bool>());
        var sections = plan["Sections"]!.AsArray();

        var appEntries = sections.Single(s => s!["Name"]!.GetValue<string>() == "apps")!["Entries"]!.AsArray();
        Assert.Equal("delete", appEntries.Single(e => e!["Key"]!.GetValue<string>() == "prune-app")!["Action"]!.GetValue<string>());

        var roleEntries = sections.Single(s => s!["Name"]!.GetValue<string>() == "roles")!["Entries"]!.AsArray();
        Assert.Contains(roleEntries, e => e!["Action"]!.GetValue<string>() == "protected");

        var userEntries = sections.Single(s => s!["Name"]!.GetValue<string>() == "users")!["Entries"]!.AsArray();
        Assert.Contains(userEntries, e => e!["Action"]!.GetValue<string>() == "protected");

        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.True(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "prune-app", ct),
                "a prune PLAN must not delete anything");
        });
    }

    [Fact]
    public async Task Apply_is_atomic_a_failing_section_rolls_back_earlier_writes()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Apps apply before roles. The role references an unknown app, so the apply
        // fails AFTER the app section already ran — ADR-0017 Phase 0 demands the
        // whole transaction rolls back and the app never materializes.
        var manifest = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "atomic-app", DisplayName = "Atomic App",
                      Permissions = new[] { new { Resource = "atomic", Action = "read" } } },
            },
            Roles = new[]
            {
                new { Name = "Broken Role", App = "no-such-app", Permissions = new object[0] },
            },
        };

        var resp = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", manifest, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Manifest.UnknownApp", await resp.Content.ReadAsStringAsync(ct));

        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.False(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "atomic-app", ct),
                "the failing apply must roll back the app created earlier in the same run");
        });

        // The realm is untouched, so the SAME manifest minus the broken role applies cleanly.
        var fixedManifest = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "atomic-app", DisplayName = "Atomic App",
                      Permissions = new[] { new { Resource = "atomic", Action = "read" } } },
            },
        };
        var retry = await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", fixedManifest, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Surface_is_gated_for_an_unauthenticated_caller()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var ct = TestContext.Current.CancellationToken;

        var anon = host.Factory.CreateClient();
        var resp = await anon.GetAsync("/api/admin/realm-config/manifest-schema", ct);

        Assert.Contains(resp.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
