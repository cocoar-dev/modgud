using BuildingBlocks.Helper;
using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Services;
using Modgud.Authorization.Membership;
using Modgud.Authorization.Commands;
using Modgud.Api.Features.Roles;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Gdpr;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
using Modgud.Domain.OAuth.Common;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;
using Modgud.Permissions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.ColdStart;

/// <summary>
/// Stage 1b: the RealmManifestApplier imports a fully-configured realm in-process by
/// reusing the canonical admin operations, resolving cross-references
/// (apps↔apis/scopes/clients/roles, groups↔users/roles) in dependency order. Proves the
/// writes land in the NEW realm's tenant database (not the control-plane/system tenant
/// the call runs under).
///
/// <para>Identity is the entity Id (ADR 0024). A manifest that creates entities referencing
/// each other declares <c>#handles</c>; one that updates carries the real ids. Nothing here
/// matches by name, which is why the manifests below always say which entity they mean.</para>
/// </summary>
public class RealmManifestApplierTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    /// <summary>A manifest id in the ShortGuid spelling the applier pins with.</summary>
    private static string Pin(Guid id) => new ShortGuid(id).ToString();

    /// <summary>A cross-reference by id — with an optional readable Key, which the apply
    /// never follows and only reports when it disagrees.</summary>
    private static ManifestRef Ref(Guid id, string? key = null) => new() { Id = Pin(id), Key = key };

    /// <summary>Reads a manifest id back out of an apply result.</summary>
    private static Guid Unpin(string raw)
    {
        Assert.True(ShortGuid.TryParse(raw, out Guid id), $"'{raw}' is not a valid id.");
        return id;
    }

    [Fact]
    public async Task Import_provisions_a_fully_configured_realm_with_resolved_cross_references()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        const string slug = "acme";
        var manifest = new RealmManifest
        {
            Apps =
            [
                new RealmManifestApp
                {
                    Slug = "acme-app",
                    DisplayName = "Acme App",
                    Permissions =
                    [
                        new RealmManifestPermission("acme", "read"),
                        new RealmManifestPermission("acme", "write"),
                    ],
                },
            ],
            Apis =
            [
                new RealmManifestApi
                {
                    Name = "acme-api",
                    DisplayName = "Acme API",
                    App = "acme-app",
                    Permissions = [new RealmManifestPermission("acme", "read")],
                },
            ],
            Scopes =
            [
                new RealmManifestScope { Name = "acme.read", DisplayName = "Acme — Read", App = "acme-app", Resources = ["acme-api"] },
            ],
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "acme-web",
                    DisplayName = "Acme Web",
                    ClientType = "confidential",
                    RedirectUris = ["https://acme.test/callback"],
                    Scopes = ["openid", "acme.read"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    Apps = ["acme-app"],
                },
            ],
            Roles =
            [
                new RealmManifestRole
                {
                    // A hand-written manifest names the entities it creates with handles —
                    // the file has no ids to point with, and inventing them would just move
                    // the collision (ADR 0024).
                    Id = "#acme-admin",
                    Name = "acme-admin",
                    App = "acme-app",
                    Permissions =
                    [
                        new RealmManifestPermission("acme", "read"),
                        new RealmManifestPermission("acme", "write"),
                    ],
                },
            ],
            Users =
            [
                new RealmManifestUser { Id = "#alice", Key = "alice", Email = "alice@acme.test", UserName = "alice", Password = "Passw0rd!23" },
            ],
            Groups =
            [
                new RealmManifestGroup { Name = "Admins", Members = ["#alice"], Roles = ["#acme-admin"] },
            ],
        };

        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        var result = await ProvisionRealmAsync(factory, Shell(slug), manifest, ct);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(slug, result.Value.Slug);
        Assert.Equal("acme.localhost", result.Value.PrimaryDomain);
        Assert.True(result.Value.ClientSecrets.ContainsKey("acme-web"));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ClientSecrets["acme-web"]));
        // The apply hands back the real ids it gave the handles, so the same file can be
        // made idempotent without exporting the realm first.
        Assert.True(ShortGuid.TryParse(result.Value.AssignedIds["#alice"], out Guid _));
        Assert.True(ShortGuid.TryParse(result.Value.AssignedIds["#acme-admin"], out Guid _));

        var realms = factory.Services.GetRequiredService<IRealmProvisioningService>();
        Assert.NotNull(await realms.GetRealmBySlugAsync(slug, ct));

        // Everything landed in the NEW realm's tenant DB (inline-consistent reads).
        await InTenantAsync(factory, slug, async sp =>
        {
            var oauth = sp.GetRequiredService<OAuthAdminService>();
            Assert.Contains((await oauth.GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct)).Items, c => c.ClientId == "acme-web");
            Assert.Contains((await oauth.GetApisAsync(new PaginationRequest { PageSize = 200 }, ct)).Items, a => a.Name == "acme-api");
            Assert.Contains((await oauth.GetScopesAsync(ct)).Items, s => s.Name == "acme.read");

            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.True(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "acme-app", ct), "app landed");

            // The role resolved its app + permissions (else CreateRole would have failed
            // and rolled the import back). Confirm it persisted with both permissions.
            var role = await session.Query<PermissionRole>().Where(r => !r.IsDeleted && r.Name == "acme-admin").SingleOrDefaultAsync(ct);
            Assert.NotNull(role);
            Assert.NotNull(role!.AppId);
            Assert.Equal(2, role.PermissionIds.Count);

            // The group resolved its member (alice → user id) and role (acme-admin → role id).
            var group = await session.Query<Group>().Where(gr => !gr.IsDeleted && gr.Name == "Admins").SingleOrDefaultAsync(ct);
            Assert.NotNull(group);
            Assert.Single(group!.MemberIds);
            Assert.Single(group.RoleIds);
        });

        // Isolation: the realm's client must NOT exist in the system tenant.
        await InTenantAsync(factory, TenantConstants.SystemTenantId, async sp =>
        {
            var oauth = sp.GetRequiredService<OAuthAdminService>();
            Assert.DoesNotContain((await oauth.GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct)).Items, c => c.ClientId == "acme-web");
        });
    }

    [Fact]
    public async Task Import_rejects_a_slug_that_already_exists()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        const string slug = "dupe";
        var manifest = new RealmManifest();

        var first = await ProvisionRealmAsync(factory, Shell(slug), manifest, ct);
        Assert.False(first.IsError, first.IsError ? first.FirstError.Description : string.Empty);

        var second = await ProvisionRealmAsync(factory, Shell(slug), manifest, ct);
        Assert.True(second.IsError);
        // The canonical create op owns this rule now — one code for a duplicate slug,
        // whichever way a realm is created.
        Assert.Equal("Realm.DuplicateSlug", second.FirstError.Code);
    }

    [Fact]
    public async Task Re_importing_a_deleted_entity_under_its_pinned_id_revives_it_even_after_a_rename()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "revive";
        var appId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        // The stage → prod story: config transferred with pinned ids, deleted on the
        // target because it caused trouble, then re-imported after the fix. The ids MUST
        // come back — consuming apps persist them as foreign keys.
        RealmManifest Manifest(string appSlug, string roleName, string groupName) => new()
        {
            Apps =
            [
                new RealmManifestApp
                {
                    Slug = appSlug, Id = new ShortGuid(appId).ToString(), DisplayName = "Revive App",
                    Permissions = [new RealmManifestPermission("rev", "read")],
                },
            ],
            Roles =
            [
                new RealmManifestRole
                {
                    Name = roleName, Id = new ShortGuid(roleId).ToString(), App = appSlug,
                    Permissions = [new RealmManifestPermission("rev", "read")],
                },
            ],
            Groups = [new RealmManifestGroup { Name = groupName, Id = Pin(groupId), Roles = [Ref(roleId, roleName)] }],
        };

        var imported = await ProvisionRealmAsync(factory, Shell(slug), Manifest("rev-app", "rev-role", "RevGroup"), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        // RENAME live in the admin UI (the manifest can't rename — its natural key IS the
        // name), THEN delete. This is the trap a natural-key-equality revive guard falls
        // into: the dead streams no longer carry the manifest's keys.
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var role = await session.Query<PermissionRole>().SingleAsync(r => !r.IsDeleted && r.Name == "rev-role", ct);
            var renamedRole = await sp.GetRequiredService<RoleAdminService>().UpdateRoleAsync(
                role.Id,
                new RolePayload("rev-role-renamed", role.Description,
                    new ShortGuid(role.AppId!.Value).ToString(), false,
                    [.. role.PermissionIds.Select(pid => new ShortGuid(pid).ToString())]),
                callerIsRealmAdmin: true, ct);
            Assert.False(renamedRole.IsError, renamedRole.IsError ? renamedRole.FirstError.Description : string.Empty);

            var group = await session.Query<Group>().SingleAsync(g => !g.IsDeleted && g.Name == "RevGroup", ct);
            var renamedGroup = await new UpdateGroupHandler(
                    session,
                    sp.GetRequiredService<IMembershipEvaluator>(),
                    sp.GetRequiredService<IPermissionService>(),
                    sp.GetRequiredService<IAutoMembershipRecalculator>())
                .Handle(new UpdateGroupCommand(group.Id, "RevGroupRenamed", group.Description,
                    [.. group.MemberIds], [.. group.RoleIds], CallerIsRealmAdmin: true), ct);
            Assert.False(renamedGroup.IsError, renamedGroup.IsError ? renamedGroup.FirstError.Description : string.Empty);
        });

        // Delete all three under their CURRENT keys (staged deletes, the normal admin path).
        var deleted = await applier.UpdateRealmAsync(slug, 
            Manifest("rev-app", "rev-role", "RevGroup") with { Apps = [], Roles = [], Groups = [] },
            deletions:
            [
                new RealmDraftDeletion("groups", "RevGroupRenamed"),
                // Roles are keyed app/name — names are unique per App only.
                new RealmDraftDeletion("roles", "rev-app/rev-role-renamed"),
                new RealmDraftDeletion("apps", "rev-app"),
            ],
            ct: ct);
        Assert.False(deleted.IsError, deleted.IsError ? deleted.FirstError.Description : string.Empty);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.True((await session.LoadAsync<App>(appId, ct))!.IsDeleted, "app soft-deleted");
            Assert.True((await session.LoadAsync<PermissionRole>(roleId, ct))!.IsDeleted, "role soft-deleted");
            Assert.True((await session.LoadAsync<Group>(groupId, ct))!.IsDeleted, "group soft-deleted");
        });

        // Re-import the ORIGINAL manifest: the pinned ids point at soft-deleted streams, so
        // the apply revives them — under the manifest's (original) names, not the renamed ones.
        var reimported = await applier.UpdateRealmAsync(slug, Manifest("rev-app", "rev-role", "RevGroup"), ct: ct);
        Assert.False(reimported.IsError, reimported.IsError ? reimported.FirstError.Description : string.Empty);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();

            var app = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "rev-app", ct);
            Assert.Equal(appId, app.Id);
            Assert.Single(app.Permissions);

            var role = await session.Query<PermissionRole>().SingleAsync(r => !r.IsDeleted && r.Name == "rev-role", ct);
            Assert.Equal(roleId, role.Id);
            Assert.Equal(appId, role.AppId);

            var group = await session.Query<Group>().SingleAsync(g => !g.IsDeleted && g.Name == "RevGroup", ct);
            Assert.Equal(groupId, group.Id);
            Assert.Equal([roleId], group.RoleIds);
        });
    }

    [Fact]
    public async Task A_pinned_id_matching_a_live_entity_updates_it_and_renames_where_the_key_is_mutable()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "idmatch";
        var appId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        RealmManifest Manifest(string roleName, string groupName, string? groupDescription = null) => new()
        {
            Apps = [new RealmManifestApp { Slug = "im-app", Id = Pin(appId), DisplayName = "Id Match App", Permissions = [new RealmManifestPermission("im", "read")] }],
            Roles =
            [
                new RealmManifestRole
                {
                    Name = roleName, Id = Pin(roleId), App = "im-app",
                    Permissions = [new RealmManifestPermission("im", "read")],
                },
            ],
            Groups =
            [
                new RealmManifestGroup
                {
                    Name = groupName, Id = Pin(groupId),
                    Description = groupDescription, Roles = [Ref(roleId, roleName)],
                },
            ],
        };

        var imported = await ProvisionRealmAsync(factory, Shell(slug), Manifest("im-role", "ImGroup"), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        // Same ids, DIFFERENT names: the id names the entity, so this is an update that
        // renames — not a second entity next to the old one.
        var renamed = await applier.UpdateRealmAsync(slug, Manifest("im-role-v2", "ImGroupV2", "renamed via id"), ct: ct);
        Assert.False(renamed.IsError, renamed.IsError ? renamed.FirstError.Description : string.Empty);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();

            var roles = await session.Query<PermissionRole>().Where(r => !r.IsDeleted && r.AppId != null).ToListAsync(ct);
            var role = Assert.Single(roles);
            Assert.Equal(roleId, role.Id);
            Assert.Equal("im-role-v2", role.Name);

            var groups = await session.Query<Group>().Where(g => !g.IsDeleted && g.Name.StartsWith("ImGroup")).ToListAsync(ct);
            var group = Assert.Single(groups);
            Assert.Equal(groupId, group.Id);
            Assert.Equal("ImGroupV2", group.Name);
            Assert.Equal("renamed via id", group.Description);
            // The group still points at the SAME role — renaming both in one apply keeps
            // the cross-reference intact (the manifest's role key moved with it).
            Assert.Equal([roleId], group.RoleIds);
        });

        // The plan makes the rename visible before it happens.
        var planner = factory.Services.GetRequiredService<RealmManifestPlanner>();
        var plan = await planner.PlanAsync(slug, Manifest("im-role-v3", "ImGroupV3"), prune: false, ct: ct);
        Assert.False(plan.IsError, plan.IsError ? plan.FirstError.Description : string.Empty);

        var roleEntry = Assert.Single(plan.Value.Sections.Single(s => s.Name == "roles").Entries);
        Assert.Equal("update", roleEntry.Action);
        Assert.Contains(roleEntry.Notes, n => n.Contains("RENAMES 'im-app/im-role-v2' to 'im-app/im-role-v3'"));
        Assert.Contains(roleEntry.Changes, c => c.Field == "Name");
        // A renamed entity is NOT also a delete candidate under its old key.
        Assert.DoesNotContain(plan.Value.Sections.Single(s => s.Name == "roles").Entries, e => e.Action == "delete");
    }

    [Fact]
    public async Task A_pinned_id_naming_an_entity_with_an_immutable_key_fails_with_both_ways_out()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "immutablekey";
        var appId = Guid.NewGuid();

        RealmManifest Manifest(string appSlug) => new()
        {
            Apps = [new RealmManifestApp { Slug = appSlug, Id = new ShortGuid(appId).ToString(), DisplayName = "App" }],
        };

        var imported = await ProvisionRealmAsync(factory, Shell(slug), Manifest("ik-app"), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        // An app slug cannot be renamed through the canonical update — the id and the slug
        // name two different things, which is never a silent merge.
        var renamed = await applier.UpdateRealmAsync(slug, Manifest("ik-app-renamed"), ct: ct);
        Assert.True(renamed.IsError);
        Assert.Equal("Manifest.ImmutableKey", renamed.FirstError.Code);

        var planner = factory.Services.GetRequiredService<RealmManifestPlanner>();
        var plan = await planner.PlanAsync(slug, Manifest("ik-app-renamed"), prune: false, ct: ct);
        var entry = Assert.Single(plan.Value.Sections.Single(s => s.Name == "apps").Entries);
        Assert.Equal("error", entry.Action);
        Assert.Contains(entry.Notes, n => n.Contains("Slug is immutable"));
    }

    [Fact]
    public async Task Pinning_an_id_owned_by_another_entity_type_fails_the_apply()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "idclash";
        var appId = Guid.NewGuid();

        RealmManifest Manifest(bool withRole) => new()
        {
            Apps = [new RealmManifestApp { Slug = "first", Id = new ShortGuid(appId).ToString(), DisplayName = "First" }],
            // Claims the APP's id for a role — appending role events onto an app's stream
            // would corrupt it, so this stays a hard conflict (no revive, no update).
            Roles = withRole
                ? [new RealmManifestRole { Name = "trespasser", Id = new ShortGuid(appId).ToString(), IsRealmAdmin = true }]
                : [],
        };

        var imported = await ProvisionRealmAsync(factory, Shell(slug), Manifest(withRole: false), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        var clash = await applier.UpdateRealmAsync(slug, Manifest(withRole: true), ct: ct);

        Assert.True(clash.IsError);
        Assert.Equal("Role.PinnedIdTaken", clash.FirstError.Code);
    }

    [Fact]
    public async Task Update_merges_in_place_keeping_ids_and_upserts_new_entities()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        const string slug = "globex";
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        // ── Import the baseline realm ──────────────────────────────────────────
        var imported = await ProvisionRealmAsync(factory, Shell(slug), BuildGlobexManifest(slug, version: 1), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        // Capture the stable ids so we can prove the update was IN PLACE (not drop+recreate).
        Guid appId = default, roleId = default, userId = default, groupId = default;
        Guid clientId = default, scopeId = default, apiId = default;
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            appId = (await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "globex-app", ct)).Id;
            roleId = (await session.Query<PermissionRole>().SingleAsync(r => !r.IsDeleted && r.Name == "globex-admin", ct)).Id;
            userId = (await session.Query<Person>().SingleAsync(p => !p.IsDeleted && p.AccountName == "alice", ct)).Id;
            groupId = (await session.Query<Group>().SingleAsync(g => !g.IsDeleted && g.Name == "Admins", ct)).Id;
            clientId = (await session.Query<OAuthApplicationState>().SingleAsync(x => !x.IsDeleted && x.ClientId == "globex-web", ct)).Id;
            scopeId = (await session.Query<OAuthScopeState>().SingleAsync(x => !x.IsDeleted && x.Name == "globex.read", ct)).Id;
            apiId = (await session.Query<OAuthApiState>().SingleAsync(x => !x.IsDeleted && x.Name == "globex-api", ct)).Id;
        });

        // ── Apply the v2 manifest: changes every existing entity + adds a new role ──
        var updated = await applier.UpdateRealmAsync(slug, BuildGlobexManifest(slug, version: 2), ct: ct);
        Assert.False(updated.IsError, updated.IsError ? updated.FirstError.Description : string.Empty);

        // The realm DB was never dropped.
        var realms = factory.Services.GetRequiredService<IRealmProvisioningService>();
        Assert.NotNull(await realms.GetRealmBySlugAsync(slug, ct));

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();

            // App: same id (in place), display name changed, catalog grew to 3.
            var app = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "globex-app", ct);
            Assert.Equal(appId, app.Id);
            Assert.Equal("Globex App v2", app.DisplayName);
            Assert.Equal(3, app.Permissions.Count);

            // Role: same id, now references all 3 permissions.
            var role = await session.Query<PermissionRole>().SingleAsync(r => !r.IsDeleted && r.Name == "globex-admin", ct);
            Assert.Equal(roleId, role.Id);
            Assert.Equal(3, role.PermissionIds.Count);

            // The brand-new role was upsert-created.
            Assert.True(await session.Query<PermissionRole>().AnyAsync(r => !r.IsDeleted && r.Name == "globex-viewer", ct));

            // User: same id, firstname now set (was null on import).
            var person = await session.Query<Person>().SingleAsync(p => !p.IsDeleted && p.AccountName == "alice", ct);
            Assert.Equal(userId, person.Id);
            Assert.Equal("Alice", person.Firstname);

            // Group: same id, description + role set replaced (now both roles).
            var group = await session.Query<Group>().SingleAsync(g => !g.IsDeleted && g.Name == "Admins", ct);
            Assert.Equal(groupId, group.Id);
            Assert.Equal("Updated admins", group.Description);
            Assert.Equal(2, group.RoleIds.Count);

            // OAuth entities kept their ids (in-place update, not recreated).
            Assert.Equal(clientId, (await session.Query<OAuthApplicationState>().SingleAsync(x => !x.IsDeleted && x.ClientId == "globex-web", ct)).Id);
            Assert.Equal(scopeId, (await session.Query<OAuthScopeState>().SingleAsync(x => !x.IsDeleted && x.Name == "globex.read", ct)).Id);
            Assert.Equal(apiId, (await session.Query<OAuthApiState>().SingleAsync(x => !x.IsDeleted && x.Name == "globex-api", ct)).Id);

            // The client's redirect URI was replaced with the v2 value.
            var oauth = sp.GetRequiredService<OAuthAdminService>();
            var client = (await oauth.GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct)).Items.Single(c => c.ClientId == "globex-web");
            Assert.Contains("https://globex.test/cb2", client.RedirectUris);
            Assert.DoesNotContain("https://globex.test/cb1", client.RedirectUris);
        });
    }

    [Fact]
    public async Task Update_omitting_a_bool_leaves_it_unchanged()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "boolpatch";
        var clientId = Guid.NewGuid();
        // Import a DISABLED confidential client (Enabled explicitly false).
        var manifest = new RealmManifest
        {
            Apps = [new RealmManifestApp { Slug = "bp-app", DisplayName = "BP", Permissions = [new RealmManifestPermission("bp", "read")] }],
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "bp-web",
                    Id = Pin(clientId),
                    ClientType = "confidential",
                    RedirectUris = ["https://bp.test/cb1"],
                    Scopes = ["openid"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    Apps = ["bp-app"],
                    Enabled = false,
                },
            ],
        };
        Assert.False((await ProvisionRealmAsync(factory, Shell(slug), manifest, ct)).IsError);

        // Apply a partial update: change the redirect URI, OMIT Enabled (null = no change).
        // The Id is what makes this the SAME client — the client_id is only its name.
        var patch = new RealmManifest
        {
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "bp-web",
                    Id = Pin(clientId),
                    ClientType = "confidential",
                    RedirectUris = ["https://bp.test/cb2"],
                    Apps = ["bp-app"],
                    // Enabled deliberately omitted.
                },
            ],
        };
        Assert.False((await applier.UpdateRealmAsync(slug, patch, ct: ct)).IsError);

        await InTenantAsync(factory, slug, async sp =>
        {
            var client = (await sp.GetRequiredService<OAuthAdminService>()
                .GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct)).Items.Single(c => c.ClientId == "bp-web");
            Assert.False(client.Enabled, "the omitted Enabled bool must not flip the disabled client back on");
            Assert.Contains("https://bp.test/cb2", client.RedirectUris);
        });
    }

    [Fact]
    public async Task Import_and_update_apply_the_client_access_token_type()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "tokentype";
        var ttWebId = Guid.NewGuid();
        RealmManifestClient Client(string? accessTokenType) => new()
        {
            ClientId = "tt-web",
            Id = Pin(ttWebId),
            ClientType = "confidential",
            RedirectUris = ["https://tt.test/cb"],
            Scopes = ["openid"],
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            AccessTokenType = accessTokenType,
        };
        var manifest = new RealmManifest
        {
            Clients = [Client("Jwt")],
        };
        Assert.False((await ProvisionRealmAsync(factory, Shell(slug), manifest, ct)).IsError);

        async Task<AccessTokenType> GetTokenTypeAsync()
        {
            var tokenType = default(AccessTokenType);
            await InTenantAsync(factory, slug, async sp =>
            {
                tokenType = (await sp.GetRequiredService<OAuthAdminService>()
                    .GetClientsAsync(new PaginationRequest { PageSize = 200 }, ct))
                    .Items.Single(c => c.ClientId == "tt-web").AccessTokenType;
            });
            return tokenType;
        }

        // Import applied the manifest value instead of silently falling back to Reference.
        Assert.Equal(AccessTokenType.Jwt, await GetTokenTypeAsync());

        // Apply with the field OMITTED: no change (same patch semantics as the bool flags).
        Assert.False((await applier.UpdateRealmAsync(slug, manifest with { Clients = [Client(null)] }, ct: ct)).IsError);
        Assert.Equal(AccessTokenType.Jwt, await GetTokenTypeAsync());

        // Apply with an explicit 'Reference': the merge flips it back.
        Assert.False((await applier.UpdateRealmAsync(slug, manifest with { Clients = [Client("Reference")] }, ct: ct)).IsError);
        Assert.Equal(AccessTokenType.Reference, await GetTokenTypeAsync());

        // An invalid value is a contextual validation error, not a silent default.
        var invalid = await applier.UpdateRealmAsync(slug, manifest with { Clients = [Client("Bogus")] }, ct: ct);
        Assert.True(invalid.IsError);
        Assert.Equal("Manifest.InvalidEnum", invalid.FirstError.Code);
    }

    [Fact]
    public async Task Update_rejects_a_slug_that_does_not_exist()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var result = await applier.UpdateRealmAsync("ghost", BuildGlobexManifest("ghost", version: 1), ct: ct);

        Assert.True(result.IsError);
        Assert.Equal("Realm.NotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task Prune_removes_absent_entities_but_protects_infra_and_admins()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "prune";
        // Pinned ids throughout: the prune manifest has to say "the SAME keep-* entities",
        // and under ADR 0024 only an id says that. It is also what makes the sweep's
        // keep-set exact — the applier keeps what it just wrote, by id.
        Guid keepApp = Guid.NewGuid(), dropApp = Guid.NewGuid();
        Guid keepApi = Guid.NewGuid(), dropApi = Guid.NewGuid();
        Guid keepScope = Guid.NewGuid(), dropScope = Guid.NewGuid();
        Guid keepClient = Guid.NewGuid(), dropClient = Guid.NewGuid();
        Guid keepRole = Guid.NewGuid(), dropRole = Guid.NewGuid(), adminRole = Guid.NewGuid();
        Guid keepUser = Guid.NewGuid(), dropUser = Guid.NewGuid(), adminUser = Guid.NewGuid();
        Guid keepGroup = Guid.NewGuid(), dropGroup = Guid.NewGuid(), adminGroup = Guid.NewGuid();

        // Import a realm with keep-* + drop-* entities AND a full admin path
        // (realm-admin role + user + group). The prune manifest will OMIT every drop-*
        // entity AND the whole admin path — drop-* must go, the admin path must survive
        // (no lockout). drop-app is referenced by drop-role/drop-api/drop.read/drop-web,
        // all dropped too → exercises reverse-dependency-order pruning.
        var full = new RealmManifest
        {
            Apps =
            [
                new RealmManifestApp { Slug = "keep-app", Id = Pin(keepApp), DisplayName = "Keep", Permissions = [new RealmManifestPermission("keep", "read")] },
                new RealmManifestApp { Slug = "drop-app", Id = Pin(dropApp), DisplayName = "Drop", Permissions = [new RealmManifestPermission("drop", "read")] },
            ],
            Apis =
            [
                new RealmManifestApi { Name = "keep-api", Id = Pin(keepApi), DisplayName = "Keep API", App = "keep-app" },
                new RealmManifestApi { Name = "drop-api", Id = Pin(dropApi), DisplayName = "Drop API", App = "drop-app" },
            ],
            Scopes =
            [
                new RealmManifestScope { Name = "keep.read", Id = Pin(keepScope), DisplayName = "Keep", App = "keep-app", Resources = ["keep-api"] },
                new RealmManifestScope { Name = "drop.read", Id = Pin(dropScope), DisplayName = "Drop", App = "drop-app", Resources = ["drop-api"] },
            ],
            Clients =
            [
                new RealmManifestClient { ClientId = "keep-web", Id = Pin(keepClient), ClientType = "confidential", RedirectUris = ["https://k.test/cb"], Scopes = ["openid"], AllowedGrantTypes = ["authorization_code"], Apps = ["keep-app"] },
                new RealmManifestClient { ClientId = "drop-web", Id = Pin(dropClient), ClientType = "confidential", RedirectUris = ["https://d.test/cb"], Scopes = ["openid"], AllowedGrantTypes = ["authorization_code"], Apps = ["drop-app"] },
            ],
            Roles =
            [
                new RealmManifestRole { Name = "keep-role", Id = Pin(keepRole), App = "keep-app", Permissions = [new RealmManifestPermission("keep", "read")] },
                new RealmManifestRole { Name = "drop-role", Id = Pin(dropRole), App = "drop-app", Permissions = [new RealmManifestPermission("drop", "read")] },
                new RealmManifestRole { Name = "super-admin", Id = Pin(adminRole), IsRealmAdmin = true },
            ],
            Users =
            [
                new RealmManifestUser { Key = "keepuser", Id = Pin(keepUser), Email = "keep@prune.test", UserName = "keepuser", Password = "Passw0rd!23" },
                new RealmManifestUser { Key = "dropuser", Id = Pin(dropUser), Email = "drop@prune.test", UserName = "dropuser", Password = "Passw0rd!23" },
                new RealmManifestUser { Key = "adminuser", Id = Pin(adminUser), Email = "admin2@prune.test", UserName = "adminuser", Password = "Passw0rd!23" },
            ],
            Groups =
            [
                new RealmManifestGroup { Name = "KeepGroup", Id = Pin(keepGroup), Members = [Ref(keepUser, "keepuser")], Roles = [Ref(keepRole, "keep-app/keep-role")] },
                new RealmManifestGroup { Name = "DropGroup", Id = Pin(dropGroup), Members = [Ref(dropUser, "dropuser")], Roles = [Ref(dropRole, "drop-app/drop-role")] },
                new RealmManifestGroup { Name = "AdminGroup", Id = Pin(adminGroup), Members = [Ref(adminUser, "adminuser")], Roles = [Ref(adminRole, "super-admin")] },
            ],
        };
        var import = await ProvisionRealmAsync(factory, Shell(slug), full, ct);
        Assert.False(import.IsError, import.IsError ? import.FirstError.Description : string.Empty);

        // The prune manifest keeps only the keep-* entities; everything else is absent.
        var keepOnly = new RealmManifest
        {
            Apps = [full.Apps[0]],
            Apis = [full.Apis[0]],
            Scopes = [full.Scopes[0]],
            Clients = [full.Clients[0]],
            Roles = [full.Roles[0]],
            Users = [full.Users[0]],
            Groups = [full.Groups[0]],
        };

        var pruned = await applier.UpdateRealmAsync(slug, keepOnly, prune: true, deletions: null, ct);
        Assert.False(pruned.IsError, pruned.IsError ? pruned.FirstError.Description : string.Empty);

        // The realm DB was never dropped.
        Assert.NotNull(await factory.Services.GetRequiredService<IRealmProvisioningService>().GetRealmBySlugAsync(slug, ct));

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var perms = sp.GetRequiredService<IPermissionService>();

            // ── Absent, non-protected entities are pruned ──────────────────────
            Assert.False(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "drop-app", ct), "drop-app pruned");
            Assert.False(await session.Query<PermissionRole>().AnyAsync(r => !r.IsDeleted && r.Name == "drop-role", ct), "drop-role pruned");
            Assert.False(await session.Query<OAuthApplicationState>().AnyAsync(x => !x.IsDeleted && x.ClientId == "drop-web", ct), "drop-web pruned");
            Assert.False(await session.Query<OAuthScopeState>().AnyAsync(x => !x.IsDeleted && x.Name == "drop.read", ct), "drop.read pruned");
            Assert.False(await session.Query<OAuthApiState>().AnyAsync(x => !x.IsDeleted && x.Name == "drop-api", ct), "drop-api pruned");
            Assert.False(await session.Query<Group>().AnyAsync(g => !g.IsDeleted && g.Name == "DropGroup", ct), "DropGroup pruned");
            // User delete is the canonical recycle-bin soft-delete (deactivate + pending),
            // so the Person survives but the ApplicationUser is deactivated.
            var dropPerson = await session.Query<Person>().SingleAsync(p => p.AccountName == "dropuser", ct);
            var dropUser = await session.LoadAsync<ApplicationUser>(dropPerson.Id, ct);
            Assert.False(dropUser!.IsActive, "dropuser binned (deactivated)");

            // ── Kept entities survive ──────────────────────────────────────────
            Assert.True(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.Slug == "keep-app", ct), "keep-app kept");
            Assert.True(await session.Query<PermissionRole>().AnyAsync(r => !r.IsDeleted && r.Name == "keep-role", ct), "keep-role kept");
            Assert.True(await session.Query<OAuthApplicationState>().AnyAsync(x => !x.IsDeleted && x.ClientId == "keep-web", ct), "keep-web kept");
            Assert.True(await session.Query<OAuthScopeState>().AnyAsync(x => !x.IsDeleted && x.Name == "keep.read", ct), "keep.read kept");
            Assert.True(await session.Query<OAuthApiState>().AnyAsync(x => !x.IsDeleted && x.Name == "keep-api", ct), "keep-api kept");
            Assert.True(await session.Query<Group>().AnyAsync(g => !g.IsDeleted && g.Name == "KeepGroup", ct), "KeepGroup kept");
            var keepPerson = await session.Query<Person>().SingleAsync(p => p.AccountName == "keepuser", ct);
            Assert.True((await session.LoadAsync<ApplicationUser>(keepPerson.Id, ct))!.IsActive, "keepuser still active");

            // ── Lockout protection: the whole admin path survives despite being omitted ──
            Assert.True(await session.Query<PermissionRole>().AnyAsync(r => !r.IsDeleted && r.Name == "super-admin", ct), "realm-admin role protected");
            Assert.True(await session.Query<Group>().AnyAsync(g => !g.IsDeleted && g.Name == "AdminGroup", ct), "admin-conferring group protected");
            var adminPerson = await session.Query<Person>().SingleAsync(p => !p.IsDeleted && p.AccountName == "adminuser", ct);
            Assert.True((await session.LoadAsync<ApplicationUser>(adminPerson.Id, ct))!.IsActive, "admin user not binned");
            Assert.True(
                await perms.HasPermissionAsync(adminPerson.Id, AppSlugs.Modgud, PermissionEvaluator.RealmAdminPermission, ct),
                "admin user retains realm:admin after prune");

            // ── Infrastructure protection ──────────────────────────────────────
            Assert.True(await session.Query<App>().AnyAsync(a => !a.IsDeleted && a.IsSystem, ct), "system app protected");
            var scopes = (await sp.GetRequiredService<OAuthAdminService>().GetScopesAsync(ct)).Items;
            Assert.Contains(scopes, s => s.Name == "openid"); // auto-seeded standard scope protected
        });
    }

    /// <summary>
    /// Regression: a user in the recycle bin keeps <c>IsDeleted=false</c> (that is what
    /// reserves their email for a restore), so the exporter still lists them and every
    /// manifest written afterwards carries them along. UpdateUserHandler refuses to edit a
    /// pending-deletion user — and because one failed op rolls the WHOLE apply transaction
    /// back, a single binned user used to block EVERY later apply with
    /// <c>User.DeletionPending</c>, including the staged deletion of any OTHER user (staged
    /// deletes apply through this same path). The entry must be skipped instead: the apply
    /// succeeds, and the binned user's profile and lifecycle state are left untouched.
    /// </summary>
    [Fact]
    public async Task A_binned_user_carried_in_the_manifest_is_skipped_instead_of_failing_the_apply()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();

        const string slug = "binned";
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        var full = new RealmManifest
        {
            Users =
            [
                new RealmManifestUser { Key = "first", Id = Pin(firstId), Email = "first@binned.test", UserName = "first", Firstname = "First", Password = "Passw0rd!23" },
                new RealmManifestUser { Key = "second", Id = Pin(secondId), Email = "second@binned.test", UserName = "second", Firstname = "Second", Password = "Passw0rd!23" },
            ],
        };
        var import = await ProvisionRealmAsync(factory, Shell(slug), full, ct);
        Assert.False(import.IsError, import.IsError ? import.FirstError.Description : string.Empty);

        // ── 1. The admin deletes "first" — the normal staged-delete path. ──────────
        var binned = await applier.UpdateRealmAsync(slug, 
            full with { Users = [full.Users[1]] },
            deletions: [new RealmDraftDeletion("users", "first")],
            ct: ct);
        Assert.False(binned.IsError, binned.IsError ? binned.FirstError.Description : string.Empty);

        // ── 2. The premise: the bin is reversible, so the export still lists them. ──
        var export = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(export.IsError, export.IsError ? export.FirstError.Description : string.Empty);
        Assert.Contains(export.Value.Users, u => u.UserName == "first");

        // ── 3. Re-applying that export must NOT fail. The manifest even carries a
        //      changed profile for the binned user — read-only means it is skipped,
        //      not applied, so the stored Firstname survives untouched. ─────────────
        var carried = export.Value with
        {
            Users = [.. export.Value.Users.Select(u =>
                u.UserName == "first" ? u with { Firstname = "Overwritten" } : u)],
        };
        var reapplied = await applier.UpdateRealmAsync(slug, carried, ct: ct);
        Assert.False(reapplied.IsError, reapplied.IsError ? reapplied.FirstError.Description : string.Empty);

        // ── 4. The real-world symptom: deleting ANOTHER user while the first sits in
        //      the bin. This is what failed in production. ────────────────────────────
        var second = await applier.UpdateRealmAsync(slug, 
            carried with { Users = [.. carried.Users.Where(u => u.UserName != "second")] },
            deletions: [new RealmDraftDeletion("users", "second")],
            ct: ct);
        Assert.False(second.IsError, second.IsError ? second.FirstError.Description : string.Empty);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();

            var first = await session.Query<Person>().SingleAsync(p => p.AccountName == "first", ct);
            Assert.Equal("First", first.Firstname);                                  // skipped, not updated
            Assert.False(first.IsDeleted, "the bin is reversible — IsDeleted stays false");
            var firstState = await session.LoadAsync<UserDeletionState>(first.Id, ct);
            Assert.True(firstState!.IsDeletionPending, "lifecycle state untouched by the apply");
            Assert.False((await session.LoadAsync<ApplicationUser>(first.Id, ct))!.IsActive);

            // And the second delete really landed rather than silently no-opping.
            var secondPerson = await session.Query<Person>().SingleAsync(p => p.AccountName == "second", ct);
            var secondState = await session.LoadAsync<UserDeletionState>(secondPerson.Id, ct);
            Assert.True(secondState!.IsDeletionPending, "the other user was binned");
            Assert.False((await session.LoadAsync<ApplicationUser>(secondPerson.Id, ct))!.IsActive);
        });
    }

    /// <summary>
    /// Builds the Globex manifest. <paramref name="version"/> 1 is the import baseline;
    /// version 2 changes every existing entity (display names, catalog, redirect, role
    /// permissions, user firstname, group membership) and adds a new "globex-viewer" role —
    /// exercising both the update and the upsert-create branch.
    /// </summary>
    /// <summary>
    /// Role names are unique per App, so the manifest READS roles as <c>app/name</c> — but
    /// it never resolves them that way (ADR 0024). An export with two "Author" roles
    /// round-trips without duplicating either because every entry carries its id; a second
    /// entry of the same name without one CREATES and is refused by the domain; and the
    /// database, not the service's pre-check, is the authority on the uniqueness the
    /// readable key rests on.
    /// </summary>
    [Fact]
    public async Task Roles_are_keyed_per_app_and_round_trip_through_export()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();
        const string slug = "authors";

        static RealmManifestApp AppOf(string appSlug) => new()
        {
            Slug = appSlug, DisplayName = appSlug, Permissions = [new RealmManifestPermission("doc", "write")],
        };
        static RealmManifestRole Author(string appSlug) => new()
        {
            // Two roles of the same NAME in one file — only a handle can tell a group which
            // one it means, which is exactly the gap handles exist for.
            Id = $"#author:{appSlug}",
            Name = "Author", App = appSlug, Permissions = [new RealmManifestPermission("doc", "write")],
        };

        var manifest = new RealmManifest
        {
            Apps = [AppOf("alpha"), AppOf("beta")],
            Roles =
            [
                Author("alpha"), Author("beta"),
                new RealmManifestRole { Id = "#reviewer", Name = "Reviewer", App = "beta", Permissions = [] },
            ],
            Groups =
            [
                new RealmManifestGroup { Name = "Alpha writers", Roles = ["#author:alpha"] },
                new RealmManifestGroup { Name = "Beta writers", Roles = ["#author:beta"] },
                new RealmManifestGroup { Name = "Reviewers", Roles = ["#reviewer"] },
            ],
        };
        var imported = await ProvisionRealmAsync(factory, Shell(slug), manifest, ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        async Task<(Guid Alpha, Guid Beta, Guid Reviewer)> RoleIdsAsync()
        {
            (Guid, Guid, Guid) ids = default;
            await InTenantAsync(factory, slug, async sp =>
            {
                var session = sp.GetRequiredService<IDocumentSession>();
                var apps = (await session.Query<App>().Where(a => !a.IsDeleted).ToListAsync(ct)).ToDictionary(a => a.Slug, a => a.Id);
                var roles = await session.Query<PermissionRole>().Where(r => !r.IsDeleted && r.AppId != null).ToListAsync(ct);
                Assert.Equal(3, roles.Count);
                ids = (roles.Single(r => r.Name == "Author" && r.AppId == apps["alpha"]).Id,
                       roles.Single(r => r.Name == "Author" && r.AppId == apps["beta"]).Id,
                       roles.Single(r => r.Name == "Reviewer").Id);
                var groups = (await session.Query<Group>().Where(g => !g.IsDeleted).ToListAsync(ct)).ToDictionary(g => g.Name);
                Assert.Equal([ids.Item1], groups["Alpha writers"].RoleIds);
                Assert.Equal([ids.Item2], groups["Beta writers"].RoleIds);
                Assert.Equal([ids.Item3], groups["Reviewers"].RoleIds);
            });
            return ids;
        }
        var before = await RoleIdsAsync();

        // Export → import is a no-op: every role matches by id/key, nothing is duplicated.
        var export = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(export.IsError, export.IsError ? export.FirstError.Description : string.Empty);
        var betaRef = Assert.Single(export.Value.Groups.Single(g => g.Name == "Beta writers").Roles!);
        Assert.Equal(("beta/Author", before.Beta), (betaRef.Key, betaRef.ParsedId));
        var reapplied = await applier.UpdateRealmAsync(slug, export.Value, ct: ct);
        Assert.False(reapplied.IsError, reapplied.IsError ? reapplied.FirstError.Description : string.Empty);
        Assert.Equal(before, await RoleIdsAsync());

        // A patch that carries the role's Id updates it — even omitting App, which the id
        // makes redundant. This is the only form that can mean "that role".
        var patch = new RealmManifest
        {
            Roles = [new RealmManifestRole { Id = Pin(before.Reviewer), Name = "Reviewer", Description = "Reviews drafts" }],
        };
        var patched = await applier.UpdateRealmAsync(slug, patch, ct: ct);
        Assert.False(patched.IsError, patched.IsError ? patched.FirstError.Description : string.Empty);
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.Equal("Reviews drafts", (await session.LoadAsync<PermissionRole>(before.Reviewer, ct))!.Description);
        });

        // Without an Id the entry is a CREATE, whatever it is called — so a name already
        // taken in that app fails loudly instead of quietly editing the role that has it.
        var byNameOnly = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            Roles = [new RealmManifestRole { Name = "Reviewer", App = "beta", Description = "Which one?" }],
        }, ct: ct);
        Assert.True(byNameOnly.IsError);
        Assert.Equal("Role.NameTaken", byNameOnly.FirstError.Code);

        // And a group reference by name is refused before anything is written — with the
        // two ways out named.
        var refByName = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            Groups = [new RealmManifestGroup { Name = "Anyone", Roles = ["beta/Author"] }],
        }, ct: ct);
        Assert.True(refByName.IsError);
        Assert.Equal("Manifest.ReferenceByName", refByName.FirstError.Code);

        // A '#handle' only means something inside the file that declares it.
        var danglingHandle = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            Groups = [new RealmManifestGroup { Name = "Anyone", Roles = ["#nowhere"] }],
        }, ct: ct);
        Assert.True(danglingHandle.IsError);
        Assert.Equal("Manifest.UnknownHandle", danglingHandle.FirstError.Code);

        // One app may not own two roles of the same name — that is the invariant the key
        // rests on (the manifest path can't even express it: `beta/Reviewer` matches the
        // existing role), so the canonical service refuses it for the live admin path too.
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var beta = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "beta", ct);
            var roleAdmin = sp.GetRequiredService<RoleAdminService>();
            var duplicate = await roleAdmin.CreateRoleAsync(
                new RolePayload("Reviewer", null, new ShortGuid(beta.Id).ToString(), false, [], null),
                callerIsRealmAdmin: true, ct);
            Assert.True(duplicate.IsError);
            Assert.Equal("Role.NameTaken", duplicate.FirstError.Code);
            // The same name in ANOTHER app is fine — that is the whole point.
            var alpha = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "alpha", ct);
            var elsewhere = await roleAdmin.CreateRoleAsync(
                new RolePayload("Reviewer", null, new ShortGuid(alpha.Id).ToString(), false, [], null),
                callerIsRealmAdmin: true, ct);
            Assert.False(elsewhere.IsError, elsewhere.IsError ? elsewhere.FirstError.Description : string.Empty);
        });

        // The DATABASE is the authority, not the service's pre-check: a duplicate written
        // past the service (the loser of a race between two writers, or any other code
        // path) is refused by the partial unique indexes — for App roles and for
        // realm-admin roles alike.
        static bool IsUniqueViolation(Exception? ex)
        {
            for (; ex is not null; ex = ex.InnerException)
                if (ex is Npgsql.PostgresException { SqlState: "23505" }) return true;
            return false;
        }
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var beta = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "beta", ct);
            var id = Guid.NewGuid();
            session.Events.StartStream(id, new Modgud.Authorization.Events.PermissionRoleCreatedEvent(
                id, "Reviewer", null, beta.Id, false, []));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.SaveChangesAsync(ct));
            Assert.True(IsUniqueViolation(ex), ex.ToString());
        });
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var owner = await sp.GetRequiredService<RoleAdminService>().CreateRoleAsync(
                new RolePayload("Realm Owner", null, null, true, [], null), callerIsRealmAdmin: true, ct);
            Assert.False(owner.IsError, owner.IsError ? owner.FirstError.Description : string.Empty);
            var id = Guid.NewGuid();
            session.Events.StartStream(id, new Modgud.Authorization.Events.PermissionRoleCreatedEvent(
                id, "Realm Owner", null, null, true, []));
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => session.SaveChangesAsync(ct));
            Assert.True(IsUniqueViolation(ex), ex.ToString());
        });
        // A soft-deleted role releases its name (the index is partial).
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var roleAdmin = sp.GetRequiredService<RoleAdminService>();
            var alpha = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "alpha", ct);
            var reviewer = await session.Query<PermissionRole>().SingleAsync(r => !r.IsDeleted && r.Name == "Reviewer" && r.AppId == alpha.Id, ct);
            Assert.False((await roleAdmin.DeleteRoleAsync(reviewer.Id, ct)).IsError);
            var again = await roleAdmin.CreateRoleAsync(
                new RolePayload("Reviewer", null, new ShortGuid(alpha.Id).ToString(), false, [], null),
                callerIsRealmAdmin: true, ct);
            Assert.False(again.IsError, again.IsError ? again.FirstError.Description : string.Empty);
        });
    }

    /// <summary>
    /// Cross-references resolve by identity and by nothing else (ADR 0024): a real id
    /// against this realm, a <c>#handle</c> against this manifest, and a bare name not at
    /// all. The <c>Key</c> beside an id is documentation — never followed, and REPORTED
    /// when it has gone stale, because a file that reads wrong is the only thing a silent
    /// resolution leaves behind. The planner compares references by the entity they name,
    /// so a rename the reference follows by id is not a change.
    /// </summary>
    [Fact]
    public async Task References_follow_the_id_and_never_the_key()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();
        var exporter = factory.Services.GetRequiredService<RealmManifestExporter>();
        var planner = factory.Services.GetRequiredService<RealmManifestPlanner>();
        const string slug = "refs";

        var realm = new CreateRealmDto
        {
            Slug = slug, DisplayName = "Refs", Domains = [$"{slug}.localhost"],
            InitialAdmin = new InitialAdminDto { UserName = "boot", Email = "boot@refs.test" },
        };
        var imported = await ProvisionRealmAsync(factory, realm, new RealmManifest
        {
            Apps = [new RealmManifestApp { Slug = "alpha", DisplayName = "alpha", Permissions = [new RealmManifestPermission("doc", "write")] },
                    new RealmManifestApp { Slug = "beta", DisplayName = "beta", Permissions = [new RealmManifestPermission("doc", "write")] }],
            Roles = [new RealmManifestRole { Id = "#alpha-author", Name = "Author", App = "alpha", Permissions = [] },
                     new RealmManifestRole { Id = "#beta-author", Name = "Author", App = "beta", Permissions = [] }],
            Users = [new RealmManifestUser { Id = "#alice", Key = "alice", Email = "alice@refs.test", UserName = "alice" }],
            // Handles: everything here is created by this same file, and two roles share the
            // name "Author" — only the handle says which one the group means.
            Groups = [new RealmManifestGroup { Id = "#beta-writers", Name = "Beta writers", Members = ["#alice"], Roles = ["#beta-author"] }],
        }, ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        // The handles come back as real ids — no export needed to learn them.
        var assigned = imported.Value.AssignedIds;
        var alphaAuthor = Unpin(assigned["#alpha-author"]);
        var betaAuthor = Unpin(assigned["#beta-author"]);
        var alice = Unpin(assigned["#alice"]);
        var betaWriters = Unpin(assigned["#beta-writers"]);

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var apps = (await session.Query<App>().Where(a => !a.IsDeleted).ToListAsync(ct)).ToDictionary(a => a.Slug, a => a.Id);
            var authors = await session.Query<PermissionRole>().Where(r => !r.IsDeleted && r.Name == "Author").ToListAsync(ct);
            Assert.Equal(alphaAuthor, authors.Single(r => r.AppId == apps["alpha"]).Id);
            Assert.Equal(betaAuthor, authors.Single(r => r.AppId == apps["beta"]).Id);
            Assert.Equal(alice, (await session.Query<Person>().SingleAsync(p => !p.IsDeleted && p.AccountName == "alice", ct)).Id);
        });
        var exportBefore = await exporter.ExportRealmAsync(slug, ct);
        Assert.False(exportBefore.IsError);

        async Task<Group> GroupAsync()
        {
            Group? group = null;
            await InTenantAsync(factory, slug, async sp =>
                group = await sp.GetRequiredService<IDocumentSession>().Query<Group>()
                    .SingleAsync(g => !g.IsDeleted && g.Name == "Beta writers", ct));
            return group!;
        }
        async Task<RealmImportResult> ApplyGroupAsync(
            List<ManifestRef>? roles = null, List<ManifestRef>? members = null)
        {
            var result = await applier.UpdateRealmAsync(slug, new RealmManifest
            {
                Groups = [new RealmManifestGroup { Id = Pin(betaWriters), Name = "Beta writers", Roles = roles, Members = members }],
            }, ct: ct);
            Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
            return result.Value;
        }

        // A stale key next to a valid id: the id wins — and the staleness is REPORTED, which
        // is the only way a reader ever learns the file no longer says what it means.
        var stale = await ApplyGroupAsync(roles: [Ref(betaAuthor, "beta/Renamed long ago")]);
        Assert.Equal([betaAuthor], (await GroupAsync()).RoleIds);
        Assert.Contains(stale.SkippedReferences,
            x => x.Contains("is 'Author', not 'beta/Renamed long ago'"));

        // An unknown id next to a perfectly good key: SKIPPED. The key is never a fallback —
        // the "alpha/Author" in this realm need not be the one the file was written against.
        var unknown = await ApplyGroupAsync(roles: [Ref(Guid.NewGuid(), "alpha/Author")]);
        Assert.Contains(unknown.SkippedReferences, x => x.Contains("no role with that id"));
        // Nothing resolved, so the stored roles survive rather than being cleared.
        Assert.Equal([betaAuthor], (await GroupAsync()).RoleIds);

        // Id only, in either spelling.
        await ApplyGroupAsync(roles: [new ManifestRef { Id = alphaAuthor.ToString() }]);
        Assert.Equal([alphaAuthor], (await GroupAsync()).RoleIds);
        // Members follow the same rule.
        await ApplyGroupAsync(members: [Ref(alice, "nobody")]);
        Assert.Equal([alice], (await GroupAsync()).MemberIds);

        // A bare string is a NAME, and a name is not resolved against the realm — even when
        // it happens to spell an id. Refused before anything is written.
        var byName = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            Groups = [new RealmManifestGroup { Id = Pin(betaWriters), Name = "Beta writers", Roles = [Pin(betaAuthor)] }],
        }, ct: ct);
        Assert.True(byName.IsError);
        Assert.Equal("Manifest.ReferenceByName", byName.FirstError.Code);
        await ApplyGroupAsync(roles: [Ref(betaAuthor)]);
        Assert.Equal([betaAuthor], (await GroupAsync()).RoleIds);

        // Rename beta's Author live. The export taken BEFORE still references it by id...
        await InTenantAsync(factory, slug, async sp =>
        {
            var role = await sp.GetRequiredService<IDocumentSession>().LoadAsync<PermissionRole>(betaAuthor, ct);
            var renamed = await sp.GetRequiredService<RoleAdminService>().UpdateRoleAsync(betaAuthor,
                new RolePayload("Writer", role!.Description, new ShortGuid(role.AppId!.Value).ToString(), false, []),
                callerIsRealmAdmin: true, ct);
            Assert.False(renamed.IsError, renamed.IsError ? renamed.FirstError.Description : string.Empty);
        });
        // ...so the plan sees no change on the group (the role entry itself flags the rename),
        // and the apply keeps the group on the SAME role, stale key notwithstanding.
        var plan = await planner.PlanAsync(slug, exportBefore.Value, prune: false, baseline: exportBefore.Value, ct: ct);
        Assert.False(plan.IsError, plan.IsError ? plan.FirstError.Description : string.Empty);
        var groupEntry = plan.Value.Sections.Single(s => s.Name == "groups").Entries.Single(e => e.Key == "Beta writers");
        Assert.Equal("unchanged", groupEntry.Action);
        Assert.Empty(groupEntry.Conflicts);
        await ApplyGroupAsync(roles: exportBefore.Value.Groups.Single(g => g.Name == "Beta writers").Roles);
        Assert.Equal([betaAuthor], (await GroupAsync()).RoleIds);
        await InTenantAsync(factory, slug, async sp =>
            Assert.Equal("Writer", (await sp.GetRequiredService<IDocumentSession>().LoadAsync<PermissionRole>(betaAuthor, ct))!.Name));
    }

    // Pinned ids: v1 CREATES under them, v2 finds the very same entities again. That is the
    // stage → prod story in miniature, and under ADR 0024 it is the only way a second
    // manifest can mean "the same entity" — a matching name would not.
    private static readonly Guid GlobexAppId = Guid.NewGuid();
    private static readonly Guid GlobexApiId = Guid.NewGuid();
    private static readonly Guid GlobexScopeId = Guid.NewGuid();
    private static readonly Guid GlobexClientId = Guid.NewGuid();
    private static readonly Guid GlobexAdminRoleId = Guid.NewGuid();
    private static readonly Guid GlobexViewerRoleId = Guid.NewGuid();
    private static readonly Guid GlobexUserId = Guid.NewGuid();
    private static readonly Guid GlobexGroupId = Guid.NewGuid();

    private static RealmManifest BuildGlobexManifest(string slug, int version)
    {
        var v2 = version == 2;
        var catalog = new List<RealmManifestPermission>
        {
            new("globex", "read"),
            new("globex", "write"),
        };
        if (v2) catalog.Add(new RealmManifestPermission("globex", "delete"));

        var roles = new List<RealmManifestRole>
        {
            new()
            {
                Name = "globex-admin",
                Id = Pin(GlobexAdminRoleId),
                App = "globex-app",
                Permissions = catalog.ToList(),
            },
        };
        if (v2)
            roles.Add(new RealmManifestRole
            {
                Name = "globex-viewer",
                Id = Pin(GlobexViewerRoleId),
                App = "globex-app",
                Permissions = [new RealmManifestPermission("globex", "read")],
            });

        return new RealmManifest
        {
            Apps =
            [
                new RealmManifestApp
                {
                    Slug = "globex-app",
                    Id = Pin(GlobexAppId),
                    DisplayName = v2 ? "Globex App v2" : "Globex App",
                    Permissions = catalog,
                },
            ],
            Apis =
            [
                new RealmManifestApi
                {
                    Name = "globex-api",
                    Id = Pin(GlobexApiId),
                    DisplayName = v2 ? "Globex API v2" : "Globex API",
                    App = "globex-app",
                    Permissions = [new RealmManifestPermission("globex", "read")],
                },
            ],
            Scopes =
            [
                new RealmManifestScope
                {
                    Name = "globex.read",
                    Id = Pin(GlobexScopeId),
                    DisplayName = v2 ? "Globex Read v2" : "Globex Read",
                    App = "globex-app",
                    Resources = ["globex-api"],
                },
            ],
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "globex-web",
                    Id = Pin(GlobexClientId),
                    DisplayName = v2 ? "Globex Web v2" : "Globex Web",
                    ClientType = "confidential",
                    RedirectUris = [v2 ? "https://globex.test/cb2" : "https://globex.test/cb1"],
                    Scopes = ["openid", "globex.read"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    Apps = ["globex-app"],
                },
            ],
            Roles = roles,
            Users =
            [
                new RealmManifestUser
                {
                    Key = "alice",
                    Id = Pin(GlobexUserId),
                    Email = "alice@globex.test",
                    UserName = "alice",
                    Firstname = v2 ? "Alice" : null,
                    Password = v2 ? null : "Passw0rd!23",
                },
            ],
            Groups =
            [
                new RealmManifestGroup
                {
                    Name = "Admins",
                    Id = Pin(GlobexGroupId),
                    Description = v2 ? "Updated admins" : "Admins",
                    Members = [Ref(GlobexUserId, "alice")],
                    Roles = v2
                        ? [Ref(GlobexAdminRoleId, "globex-app/globex-admin"), Ref(GlobexViewerRoleId, "globex-app/globex-viewer")]
                        : [Ref(GlobexAdminRoleId, "globex-app/globex-admin")],
                },
            ],
        };
    }

    /// <summary>
    /// A manifest travels, so its references routinely name things the target realm does not
    /// have. The apply resolves what it can and SKIPS the rest instead of failing — a client
    /// arrives without the app that is not here, a group gets the roles that exist. What was
    /// skipped is reported, because the result alone cannot show it (a group left with two of
    /// three roles looks exactly like a group that asked for two).
    /// </summary>
    [Fact]
    public async Task Unresolvable_references_are_skipped_and_reported_rather_than_failing_the_apply()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "skipref";
        Guid readerRole = Guid.NewGuid(), alice = Guid.NewGuid(), readers = Guid.NewGuid();
        // Ids nothing in this realm carries — the "names something the target does not have"
        // case, now expressed the only way identity can express it.
        Guid ghostRole = Guid.NewGuid(), nobody = Guid.NewGuid();
        var seed = new RealmManifest
        {
            Apps =
            [
                new RealmManifestApp { Slug = "here", DisplayName = "Here",
                    Permissions = [new RealmManifestPermission("doc", "read")] },
            ],
            Roles = [new RealmManifestRole { Name = "Reader", Id = Pin(readerRole), App = "here",
                Permissions = [new RealmManifestPermission("doc", "read")] }],
            Users = [new RealmManifestUser { Key = "alice", Id = Pin(alice), Email = "alice@skipref.test", UserName = "alice" }],
            Groups = [new RealmManifestGroup { Name = "Readers", Id = Pin(readers),
                Roles = [Ref(readerRole, "here/Reader")], Members = [Ref(alice, "alice")] }],
        };
        var seeded = await ProvisionRealmAsync(factory, Shell(slug), seed, ct);
        Assert.False(seeded.IsError, seeded.IsError ? seeded.FirstError.Description : string.Empty);

        // Everything below names something this realm does NOT have, alongside something it does.
        var mixed = new RealmManifest
        {
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "mixed-web", DisplayName = "Mixed", ClientType = "public",
                    RedirectUris = ["https://mixed.test/cb"], Scopes = ["openid"],
                    AllowedGrantTypes = ["authorization_code"],
                    Apps = ["here", "elsewhere"],          // one known, one not
                },
            ],
            Roles =
            [
                new RealmManifestRole { Name = "Reader", Id = Pin(readerRole), App = "here",
                    Permissions =
                    [
                        new RealmManifestPermission("doc", "read"),      // in the catalog
                        new RealmManifestPermission("doc", "publish"),   // not in the catalog
                    ] },
            ],
            Groups =
            [
                new RealmManifestGroup { Name = "Readers", Id = Pin(readers),
                    Roles = [Ref(readerRole, "here/Reader"), Ref(ghostRole, "here/Ghost")],
                    Members = [Ref(alice, "alice"), Ref(nobody, "nobody")] },
            ],
        };

        var applied = await applier.UpdateRealmAsync(slug, mixed, ct: ct);
        Assert.False(applied.IsError, applied.IsError ? applied.FirstError.Description : string.Empty);

        var skipped = applied.Value.SkippedReferences;
        Assert.Contains(skipped, x => x.Contains("elsewhere"));
        Assert.Contains(skipped, x => x.Contains("doc:publish"));
        Assert.Contains(skipped, x => x.Contains("Ghost"));
        Assert.Contains(skipped, x => x.Contains("nobody"));

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();

            // The client exists and is linked to the app that DOES exist here.
            var app = await session.Query<App>().SingleAsync(a => !a.IsDeleted && a.Slug == "here", ct);
            var client = await session.Query<OAuthApplicationState>()
                .SingleAsync(c => !c.IsDeleted && c.ClientId == "mixed-web", ct);
            Assert.Equal([app.Id], client.AppIds);

            // The role kept the permission that resolved; the unknown one just dropped.
            var role = await session.Query<PermissionRole>().SingleAsync(r => !r.IsDeleted && r.Name == "Reader", ct);
            Assert.Single(role.PermissionIds);

            // The group kept the resolvable role + member.
            var group = await session.Query<Group>().SingleAsync(g => !g.IsDeleted && g.Name == "Readers", ct);
            Assert.Equal([role.Id], group.RoleIds);
            Assert.Single(group.MemberIds);
        });
    }

    /// <summary>
    /// A reference list is a REPLACE, and skipping does not turn it into a merge: applying
    /// roles [A, B, C] to a group that holds [A, B, D] where C does not exist here leaves
    /// [A, B] — C is skipped because it cannot resolve, D goes because the manifest did not
    /// ask for it. Anything else would make removal via manifest impossible.
    /// </summary>
    [Fact]
    public async Task A_reference_list_stays_a_replace_even_when_part_of_it_is_skipped()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "replacelist";
        Guid roleA = Guid.NewGuid(), roleB = Guid.NewGuid(), roleD = Guid.NewGuid(), groupG = Guid.NewGuid();
        // C is an id this realm does not have — the reference that will be skipped.
        var roleC = Guid.NewGuid();
        RealmManifestRole Role(string name, Guid id) => new()
        {
            Name = name, Id = Pin(id), App = "rlist",
            Permissions = [new RealmManifestPermission("doc", "read")],
        };
        var seed = new RealmManifest
        {
            Apps = [new RealmManifestApp { Slug = "rlist", DisplayName = "RList",
                Permissions = [new RealmManifestPermission("doc", "read")] }],
            Roles = [Role("A", roleA), Role("B", roleB), Role("D", roleD)],
            Groups = [new RealmManifestGroup { Name = "G", Id = Pin(groupG),
                Roles = [Ref(roleA, "rlist/A"), Ref(roleB, "rlist/B"), Ref(roleD, "rlist/D")] }],
        };
        var seeded = await ProvisionRealmAsync(factory, Shell(slug), seed, ct);
        Assert.False(seeded.IsError, seeded.IsError ? seeded.FirstError.Description : string.Empty);

        var applied = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            Groups = [new RealmManifestGroup { Name = "G", Id = Pin(groupG),
                Roles = [Ref(roleA, "rlist/A"), Ref(roleB, "rlist/B"), Ref(roleC, "rlist/C")] }],
        }, ct: ct);
        Assert.False(applied.IsError, applied.IsError ? applied.FirstError.Description : string.Empty);
        Assert.Contains(applied.Value.SkippedReferences, x => x.Contains("rlist/C"));

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var roles = (await session.Query<PermissionRole>().Where(r => !r.IsDeleted).ToListAsync(ct))
                .ToDictionary(r => r.Name, r => r.Id, StringComparer.Ordinal);
            var group = await session.Query<Group>().SingleAsync(g => !g.IsDeleted && g.Name == "G", ct);

            Assert.Equal([roles["A"], roles["B"]], group.RoleIds);   // C skipped, D removed
        });
    }

    /// <summary>
    /// The one carve-out: when NOTHING in a non-empty reference list resolves, the field is
    /// left unchanged instead of written empty. An empty list is an instruction ("clear
    /// this") and a failed lookup is not one — otherwise exporting a role without its app
    /// would silently strip an existing role of every permission on the target.
    /// </summary>
    [Fact]
    public async Task A_list_that_resolves_to_nothing_leaves_the_stored_value_alone()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;
        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        const string slug = "nothingresolves";
        var seed = new RealmManifest
        {
            Apps = [new RealmManifestApp { Slug = "keep", DisplayName = "Keep",
                Permissions = [new RealmManifestPermission("doc", "read"), new RealmManifestPermission("doc", "write")] }],
            Roles = [new RealmManifestRole { Name = "Editor", App = "keep",
                Permissions = [new RealmManifestPermission("doc", "read"), new RealmManifestPermission("doc", "write")] }],
        };
        var seeded = await ProvisionRealmAsync(factory, Shell(slug), seed, ct);
        Assert.False(seeded.IsError, seeded.IsError ? seeded.FirstError.Description : string.Empty);

        Guid roleId = default;
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var role = await session.Query<PermissionRole>().SingleAsync(r => !r.IsDeleted && r.Name == "Editor", ct);
            roleId = role.Id;
            Assert.Equal(2, role.PermissionIds.Count);
        });

        // The same role — matched by its exported Id, which is what makes this the SAME
        // entity even though its App no longer resolves here (an app slug is part of a
        // role's natural key, so without the id this would read as a different role).
        var orphaned = await applier.UpdateRealmAsync(slug, new RealmManifest
        {
            Roles = [new RealmManifestRole
            {
                Id = new ShortGuid(roleId).ToString(),
                Name = "Editor", App = "gone",
                Permissions = [new RealmManifestPermission("doc", "read")],
            }],
        }, ct: ct);
        Assert.False(orphaned.IsError, orphaned.IsError ? orphaned.FirstError.Description : string.Empty);
        Assert.Contains(orphaned.Value.SkippedReferences, x => x.Contains("app 'gone'"));

        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var role = await session.LoadAsync<PermissionRole>(roleId, ct);
            Assert.Equal(2, role!.PermissionIds.Count);   // NOT stripped
        });
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
