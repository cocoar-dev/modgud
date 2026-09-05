using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Principals;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// ADR-0005 Phase 1: named server-side drafts under /api/admin/realm-config/drafts —
/// the staging documents of the draft workspace. Covered here: the draft lifecycle
/// with write-only secret slots, the baseline-anchored three-way conflict gate on
/// apply (stale-overwrite detection + resolution), and optimistic version conflicts
/// for collaborative editing.
/// </summary>
public class RealmDraftEndpointsTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Draft_lifecycle_stages_secrets_write_only_and_applies_them()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Create a draft from an uploaded manifest carrying a user password.
        var create = new
        {
            Name = "Onboarding wave",
            Source = "manifest",
            Manifest = new
            {
                Realm = new { },
                Users = new[]
                {
                    new { Email = "draft-user@example.com", UserName = "draftuser",
                          Firstname = "Draft", Password = "Secret12ab!" },
                },
            },
        };
        var created = await client.PostAsJsonAsync(
            "/api/admin/realm-config/drafts", create, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var draft = JsonNode.Parse(await created.Content.ReadAsStringAsync(ct))!;
        var draftId = draft["Id"]!.GetValue<Guid>();

        // The stored manifest is sanitized: no password value, but the slot is listed.
        Assert.Null(draft["Manifest"]!["Users"]![0]!["Password"]?.GetValue<string>());
        Assert.Contains("users/draftuser/Password",
            draft["SecretSlots"]!.AsArray().Select(s => s!.GetValue<string>()));

        // The plan merges the secret back in memory: the redacted password note shows.
        var planResp = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/plan", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, planResp.StatusCode);
        var plan = JsonNode.Parse(await planResp.Content.ReadAsStringAsync(ct))!;
        var userEntry = plan["Sections"]!.AsArray()
            .Single(s => s!["Name"]!.GetValue<string>() == "users")!["Entries"]!.AsArray()
            .Single(e => e!["Key"]!.GetValue<string>() == "draftuser")!;
        Assert.Equal("create", userEntry["Action"]!.GetValue<string>());
        Assert.Contains(userEntry["Notes"]!.AsArray(),
            n => n!.GetValue<string>().Contains("Password will be set at create"));
        Assert.False(plan["HasConflicts"]!.GetValue<bool>());

        // Apply consumes the draft; the user exists afterwards.
        var applyResp = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/apply", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);

        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.True(await session.Query<Person>()
                .AnyAsync(p => !p.IsDeleted && p.AccountName == "draftuser", ct));
        });

        var gone = await client.GetAsync($"/api/admin/realm-config/drafts/{draftId}", ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Apply_is_gated_on_three_way_conflicts_until_resolved()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Seed an app, then draft from the export (baseline = app @ "V1").
        var seed = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "cfl-app", DisplayName = "V1",
                      Permissions = new[] { new { Resource = "cfl", Action = "read" } } },
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", seed, factory.JsonOptions, ct)).StatusCode);

        var created = await client.PostAsJsonAsync("/api/admin/realm-config/drafts",
            new { Name = "Conflict draft", Source = "export" }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var draft = JsonNode.Parse(await created.Content.ReadAsStringAsync(ct))!;
        var draftId = draft["Id"]!.GetValue<Guid>();

        // Live moves on while the draft is open: the app is renamed directly.
        var liveChange = new
        {
            Realm = new { },
            Apps = new[]
            {
                new { Slug = "cfl-app", DisplayName = "Live V2",
                      Permissions = new[] { new { Resource = "cfl", Action = "read" } } },
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", liveChange, factory.JsonOptions, ct)).StatusCode);

        // The draft still carries the baseline "V1" — applying would silently revert
        // the live rename. The plan flags it as a staleOverwrite conflict...
        var planResp = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/plan", new { }, factory.JsonOptions, ct);
        var plan = JsonNode.Parse(await planResp.Content.ReadAsStringAsync(ct))!;
        Assert.True(plan["HasConflicts"]!.GetValue<bool>());
        var appEntry = plan["Sections"]!.AsArray()
            .Single(s => s!["Name"]!.GetValue<string>() == "apps")!["Entries"]!.AsArray()
            .Single(e => e!["Key"]!.GetValue<string>() == "cfl-app")!;
        var conflict = appEntry["Conflicts"]!.AsArray()
            .Single(c => c!["Field"]?.GetValue<string>() == "DisplayName")!;
        Assert.Equal("staleOverwrite", conflict["Kind"]!.GetValue<string>());
        Assert.Equal("V1", conflict["Baseline"]!.GetValue<string>());
        Assert.Equal("Live V2", conflict["Live"]!.GetValue<string>());

        // ...and the apply gate refuses with the plan attached.
        var refused = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/apply", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Draft.ApplyRefused", await refused.Content.ReadAsStringAsync(ct));

        // Resolution "take live": update the draft's manifest to the live value.
        var manifest = draft["Manifest"]!.DeepClone()!.AsObject();
        manifest["Apps"]!.AsArray()
            .Single(a => a!["Slug"]!.GetValue<string>() == "cfl-app")!["DisplayName"] = "Live V2";
        var resolved = await client.PutAsJsonAsync($"/api/admin/realm-config/drafts/{draftId}",
            new { ExpectedVersion = 1, Manifest = manifest }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        var applied = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/apply", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
    }

    [Fact]
    public async Task Implicit_active_draft_lifecycle_commit_park_switch_apply()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // No active draft to begin with.
        await AssertNoActiveDraftAsync(client, ct);

        // First "commit": staging one entity implicitly creates an auto-named draft.
        var stage = await client.PutAsJsonAsync("/api/admin/realm-config/drafts/active/entities/users",
            new { Email = "implicit@example.com", UserName = "implicit", Firstname = "Auto" },
            factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, stage.StatusCode);
        var draft = JsonNode.Parse(await stage.Content.ReadAsStringAsync(ct))!;
        var draftId = draft["Id"]!.GetValue<Guid>();
        Assert.Contains("·", draft["Name"]!.GetValue<string>());
        Assert.Contains(draft["Manifest"]!["Users"]!.AsArray(),
            u => u!["UserName"]?.GetValue<string>() == "implicit");

        // Parking clears the checkout but keeps the branch.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/admin/realm-config/drafts/active/park", new { }, factory.JsonOptions, ct)).StatusCode);
        await AssertNoActiveDraftAsync(client, ct);

        // A commit while parked starts a SECOND implicit draft (the quick-fix branch)...
        var quickFix = await client.PutAsJsonAsync("/api/admin/realm-config/drafts/active/entities/apps",
            new { Slug = "quickfix-app", DisplayName = "Quick Fix",
                  Permissions = new[] { new { Resource = "qf", Action = "read" } } },
            factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, quickFix.StatusCode);
        var quickFixId = JsonNode.Parse(await quickFix.Content.ReadAsStringAsync(ct))!["Id"]!.GetValue<Guid>();
        Assert.NotEqual(draftId, quickFixId);

        // ...which applies and clears the pointer (push + merge to main).
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{quickFixId}/apply", new { }, factory.JsonOptions, ct)).StatusCode);
        await AssertNoActiveDraftAsync(client, ct);

        // Switch back to the parked branch — its staged user is still there — and apply it.
        var switched = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/active/switch/{draftId}", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/apply", new { }, factory.JsonOptions, ct)).StatusCode);

        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.True(await session.Query<Person>()
                .AnyAsync(p => !p.IsDeleted && p.AccountName == "implicit", ct));
            Assert.True(await session.Query<Modgud.Authorization.Apps.App>()
                .AnyAsync(a => !a.IsDeleted && a.Slug == "quickfix-app", ct));
        });
    }

    [Fact]
    public async Task Staged_deletes_stage_undo_apply_and_protect_admin_targets()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Seed two scopes live — one to delete, one that must survive the targeted apply.
        var seed = new
        {
            Realm = new { },
            Scopes = new[]
            {
                new { Name = "sd-doomed", DisplayName = "Doomed" },
                new { Name = "sd-survivor", DisplayName = "Survivor" },
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/admin/realm-config/apply", seed, factory.JsonOptions, ct)).StatusCode);

        // Staging a deletion implicitly creates the draft: the entity leaves the
        // manifest and the (section, key) is recorded.
        var staged = await client.PutAsJsonAsync(
            "/api/admin/realm-config/drafts/active/deletions/scopes?key=sd-doomed",
            new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var draft = JsonNode.Parse(await staged.Content.ReadAsStringAsync(ct))!;
        var draftId = draft["Id"]!.GetValue<Guid>();
        Assert.Contains(draft["Deletions"]!.AsArray(),
            d => d!["Key"]!.GetValue<string>() == "sd-doomed");
        Assert.DoesNotContain(draft["Manifest"]!["Scopes"]!.AsArray(),
            s => s!["Name"]!.GetValue<string>() == "sd-doomed");

        // The plan shows the targeted delete without prune, conflict-free.
        var plan = await PlanAsync(client, factory, draftId, ct);
        var doomed = SectionEntry(plan, "scopes", "sd-doomed");
        Assert.Equal("delete", doomed["Action"]!.GetValue<string>());
        Assert.False(plan["HasConflicts"]!.GetValue<bool>());

        // Undo restores the entity from the baseline — the plan reads unchanged again.
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(
            "/api/admin/realm-config/drafts/active/deletions/scopes?key=sd-doomed", ct)).StatusCode);
        plan = await PlanAsync(client, factory, draftId, ct);
        Assert.Equal("unchanged", SectionEntry(plan, "scopes", "sd-doomed")["Action"]!.GetValue<string>());

        // Re-stage and apply: exactly the targeted scope is gone, the survivor stays.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            "/api/admin/realm-config/drafts/active/deletions/scopes?key=sd-doomed",
            new { }, factory.JsonOptions, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/apply", new { }, factory.JsonOptions, ct)).StatusCode);

        var export = JsonNode.Parse(await client.GetStringAsync("/api/admin/realm-config/export", ct))!;
        var scopeNames = export["Scopes"]!.AsArray().Select(s => s!["Name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("sd-doomed", scopeNames);
        Assert.Contains("sd-survivor", scopeNames);

        // A staged deletion of a protected entity (a realm-admin role) is an apply
        // ERROR — the admin explicitly asked for something the applier will never do.
        var adminRole = export["Roles"]!.AsArray()
            .First(r => r!["IsRealmAdmin"]!.GetValue<bool>())!["Name"]!.GetValue<string>();
        var protectedStage = await client.PutAsJsonAsync(
            $"/api/admin/realm-config/drafts/active/deletions/roles?key={Uri.EscapeDataString(adminRole)}",
            new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, protectedStage.StatusCode);
        var protectedDraftId = JsonNode.Parse(
            await protectedStage.Content.ReadAsStringAsync(ct))!["Id"]!.GetValue<Guid>();

        plan = await PlanAsync(client, factory, protectedDraftId, ct);
        Assert.Equal("error", SectionEntry(plan, "roles", adminRole)["Action"]!.GetValue<string>());
        var refused = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{protectedDraftId}/apply", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Draft.ApplyRefused", await refused.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Regression: role names are unique PER APP, so two apps may each own an "Author". The
    /// planner used to key roles by bare name and crashed (500, duplicate dictionary key) on
    /// EVERY plan of such a realm — no draft could be planned or applied at all. Roles are
    /// now keyed <c>app/name</c> end to end: export, staging seam, plan, apply, group refs.
    /// </summary>
    [Fact]
    public async Task Roles_sharing_a_name_across_apps_are_keyed_app_slash_name()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        // Live state: two apps, each with an "Author" role; a group holding beta's.
        var seed = new
        {
            Name = "Two authors",
            Source = "manifest",
            Manifest = new
            {
                Realm = new { },
                Apps = new[]
                {
                    new { Slug = "alpha", DisplayName = "Alpha", Permissions = new[] { new { Resource = "doc", Action = "write" } } },
                    new { Slug = "beta", DisplayName = "Beta", Permissions = new[] { new { Resource = "doc", Action = "write" } } },
                },
                Roles = new[]
                {
                    new { Name = "Author", App = "alpha", Permissions = new[] { new { Resource = "doc", Action = "write" } } },
                    new { Name = "Author", App = "beta", Permissions = new[] { new { Resource = "doc", Action = "write" } } },
                },
                Groups = new[] { new { Name = "Beta writers", Roles = new[] { "beta/Author" } } },
            },
        };
        var created = await client.PostAsJsonAsync("/api/admin/realm-config/drafts", seed, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var seedId = JsonNode.Parse(await created.Content.ReadAsStringAsync(ct))!["Id"]!.GetValue<Guid>();
        var seedPlan = await PlanAsync(client, factory, seedId, ct);
        Assert.Equal("create", SectionEntry(seedPlan, "roles", "alpha/Author")["Action"]!.GetValue<string>());
        Assert.Equal("create", SectionEntry(seedPlan, "roles", "beta/Author")["Action"]!.GetValue<string>());
        var seeded = await client.PostAsJsonAsync($"/api/admin/realm-config/drafts/{seedId}/apply", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, seeded.StatusCode);

        Guid alphaAuthor = default, betaAuthor = default;
        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var apps = await session.Query<Modgud.Authorization.Apps.App>().Where(a => !a.IsDeleted).ToListAsync(ct);
            var authors = await session.Query<Modgud.Authorization.Roles.PermissionRole>()
                .Where(r => !r.IsDeleted && r.Name == "Author").ToListAsync(ct);
            Assert.Equal(2, authors.Count);
            alphaAuthor = authors.Single(r => r.AppId == apps.Single(a => a.Slug == "alpha").Id).Id;
            betaAuthor = authors.Single(r => r.AppId == apps.Single(a => a.Slug == "beta").Id).Id;
            var group = await session.Query<Group>()
                .SingleAsync(g => !g.IsDeleted && g.Name == "Beta writers", ct);
            Assert.Equal([betaAuthor], group.RoleIds);
        });

        // The export references roles by their qualified key.
        var export = JsonNode.Parse(await client.GetStringAsync("/api/admin/realm-config/export", ct))!;
        var exportedGroup = export["Groups"]!.AsArray().Single(g => g!["Name"]!.GetValue<string>() == "Beta writers")!;
        Assert.Equal(["beta/Author"], exportedGroup["Roles"]!.AsArray().Select(r => r!.GetValue<string>()));

        // Stage an edit of beta's Author exactly as the admin UI does (Id + pinned Key +
        // App) — this plan used to be the 500.
        var stage = await client.PutAsJsonAsync("/api/admin/realm-config/drafts/active/entities/roles",
            new { Key = "beta/Author", Id = betaAuthor, Name = "Author", App = "beta", Description = "Beta's authors" },
            factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, stage.StatusCode);
        var draftId = JsonNode.Parse(await stage.Content.ReadAsStringAsync(ct))!["Id"]!.GetValue<Guid>();

        var plan = await PlanAsync(client, factory, draftId, ct);
        var roleEntries = plan["Sections"]!.AsArray()
            .Single(s => s!["Name"]!.GetValue<string>() == "roles")!["Entries"]!.AsArray()
            .ToDictionary(e => e!["Key"]!.GetValue<string>(), e => e!["Action"]!.GetValue<string>());
        Assert.Equal("update", roleEntries["beta/Author"]);
        Assert.Equal("unchanged", roleEntries["alpha/Author"]);
        Assert.DoesNotContain("Author", roleEntries.Keys);
        Assert.False(plan["HasConflicts"]!.GetValue<bool>());

        var applied = await client.PostAsJsonAsync($"/api/admin/realm-config/drafts/{draftId}/apply", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.Equal("Beta's authors", (await session.LoadAsync<Modgud.Authorization.Roles.PermissionRole>(betaAuthor, ct))!.Description);
            Assert.Null((await session.LoadAsync<Modgud.Authorization.Roles.PermissionRole>(alphaAuthor, ct))!.Description);
        });

        // A group referencing the bare "Author" is ambiguous — the apply refuses instead
        // of silently picking one of the two.
        var ambiguous = await client.PutAsJsonAsync("/api/admin/realm-config/drafts/active/entities/groups",
            new { Name = "Ambiguous", Roles = new[] { "Author" } }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, ambiguous.StatusCode);
        var ambiguousId = JsonNode.Parse(await ambiguous.Content.ReadAsStringAsync(ct))!["Id"]!.GetValue<Guid>();
        var refused = await client.PostAsJsonAsync($"/api/admin/realm-config/drafts/{ambiguousId}/apply", new { }, factory.JsonOptions, ct);
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Manifest.AmbiguousReference", await refused.Content.ReadAsStringAsync(ct));
    }

    private static async Task<JsonNode> PlanAsync(
        HttpClient client, ColdStartWebApplicationFactory factory, Guid draftId, CancellationToken ct)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}/plan", new { }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
    }

    private static JsonNode SectionEntry(JsonNode plan, string section, string key)
        => plan["Sections"]!.AsArray()
            .Single(s => s!["Name"]!.GetValue<string>() == section)!["Entries"]!.AsArray()
            .Single(e => e!["Key"]!.GetValue<string>() == key)!;

    [Fact]
    public async Task Concurrent_edits_hit_the_optimistic_version_gate()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var client = await factory.CreateRealmAdminAndLoginAsync();

        var created = await client.PostAsJsonAsync("/api/admin/realm-config/drafts",
            new { Name = "Race draft", Source = "empty" }, factory.JsonOptions, ct);
        var draftId = JsonNode.Parse(await created.Content.ReadAsStringAsync(ct))!["Id"]!.GetValue<Guid>();

        // First writer bumps the version...
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(
            $"/api/admin/realm-config/drafts/{draftId}",
            new { ExpectedVersion = 1, Name = "Race draft (renamed)" }, factory.JsonOptions, ct)).StatusCode);

        // ...so the second writer's stale version is refused with a conflict.
        var stale = await client.PutAsJsonAsync($"/api/admin/realm-config/drafts/{draftId}",
            new { ExpectedVersion = 1, Name = "Lost update" }, factory.JsonOptions, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("Draft.VersionConflict", await stale.Content.ReadAsStringAsync(ct));
    }

    private static async Task AssertNoActiveDraftAsync(HttpClient client, CancellationToken ct)
    {
        // Results.Ok(null) renders an empty body; a JSON "null" is equally fine.
        var body = (await client.GetStringAsync("/api/admin/realm-config/drafts/active", ct)).Trim();
        Assert.True(body is "" or "null", $"expected no active draft, got: {body}");
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
