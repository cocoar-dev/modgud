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
/// reusing the canonical admin operations, resolving key-based cross-references
/// (apps↔apis/scopes/clients/roles, groups↔users/roles) in dependency order. Proves the
/// writes land in the NEW realm's tenant database (not the control-plane/system tenant
/// the call runs under).
/// </summary>
public class RealmManifestApplierTests(ColdStartFixture fixture) : ColdStartTestBase(fixture)
{
    [Fact]
    public async Task Import_provisions_a_fully_configured_realm_with_resolved_cross_references()
    {
        await using var host = await Fixture.CreateIsolatedHostAsync();
        var factory = host.Factory;
        var ct = TestContext.Current.CancellationToken;

        const string slug = "acme";
        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Acme",
                Domains = ["acme.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@acme.test" },
            },
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
                new RealmManifestUser { Key = "alice", Email = "alice@acme.test", UserName = "alice", Password = "Passw0rd!23" },
            ],
            Groups =
            [
                new RealmManifestGroup { Name = "Admins", Members = ["alice"], Roles = ["acme-admin"] },
            ],
        };

        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        var result = await applier.ImportNewRealmAsync(manifest, ct);

        Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        Assert.Equal(slug, result.Value.Slug);
        Assert.Equal("acme.localhost", result.Value.PrimaryDomain);
        Assert.True(result.Value.ClientSecrets.ContainsKey("acme-web"));
        Assert.False(string.IsNullOrWhiteSpace(result.Value.ClientSecrets["acme-web"]));

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

        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = "dup",
                DisplayName = "Dup",
                Domains = ["dup.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = "admin@dup.test" },
            },
        };

        var applier = factory.Services.GetRequiredService<RealmManifestApplier>();

        var first = await applier.ImportNewRealmAsync(manifest, ct);
        Assert.False(first.IsError, first.IsError ? first.FirstError.Description : string.Empty);

        var second = await applier.ImportNewRealmAsync(manifest, ct);
        Assert.True(second.IsError);
        Assert.Equal("Realm.AlreadyExists", second.FirstError.Code);
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
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Revive",
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "boot", Email = "boot@revive.test" },
            },
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
            Groups = [new RealmManifestGroup { Name = groupName, Id = new ShortGuid(groupId).ToString(), Roles = [roleName] }],
        };

        var imported = await applier.ImportNewRealmAsync(Manifest("rev-app", "rev-role", "RevGroup"), ct);
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
        var deleted = await applier.UpdateRealmAsync(
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
        var reimported = await applier.UpdateRealmAsync(Manifest("rev-app", "rev-role", "RevGroup"), ct: ct);
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
        var roleId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        RealmManifest Manifest(string roleName, string groupName, string? groupDescription = null) => new()
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Id match",
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "boot", Email = "boot@idmatch.test" },
            },
            Apps = [new RealmManifestApp { Slug = "im-app", DisplayName = "Id Match App", Permissions = [new RealmManifestPermission("im", "read")] }],
            Roles =
            [
                new RealmManifestRole
                {
                    Name = roleName, Id = new ShortGuid(roleId).ToString(), App = "im-app",
                    Permissions = [new RealmManifestPermission("im", "read")],
                },
            ],
            Groups =
            [
                new RealmManifestGroup
                {
                    Name = groupName, Id = new ShortGuid(groupId).ToString(),
                    Description = groupDescription, Roles = [roleName],
                },
            ],
        };

        var imported = await applier.ImportNewRealmAsync(Manifest("im-role", "ImGroup"), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        // Same ids, DIFFERENT names: the id names the entity, so this is an update that
        // renames — not a second entity next to the old one.
        var renamed = await applier.UpdateRealmAsync(Manifest("im-role-v2", "ImGroupV2", "renamed via id"), ct: ct);
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
        var plan = await planner.PlanAsync(Manifest("im-role-v3", "ImGroupV3"), prune: false, ct: ct);
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
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Immutable key",
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "boot", Email = "boot@immutablekey.test" },
            },
            Apps = [new RealmManifestApp { Slug = appSlug, Id = new ShortGuid(appId).ToString(), DisplayName = "App" }],
        };

        var imported = await applier.ImportNewRealmAsync(Manifest("ik-app"), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        // An app slug cannot be renamed through the canonical update — the id and the slug
        // name two different things, which is never a silent merge.
        var renamed = await applier.UpdateRealmAsync(Manifest("ik-app-renamed"), ct: ct);
        Assert.True(renamed.IsError);
        Assert.Equal("Manifest.ImmutableKey", renamed.FirstError.Code);

        var planner = factory.Services.GetRequiredService<RealmManifestPlanner>();
        var plan = await planner.PlanAsync(Manifest("ik-app-renamed"), prune: false, ct: ct);
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
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Id clash",
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "boot", Email = "boot@idclash.test" },
            },
            Apps = [new RealmManifestApp { Slug = "first", Id = new ShortGuid(appId).ToString(), DisplayName = "First" }],
            // Claims the APP's id for a role — appending role events onto an app's stream
            // would corrupt it, so this stays a hard conflict (no revive, no update).
            Roles = withRole
                ? [new RealmManifestRole { Name = "trespasser", Id = new ShortGuid(appId).ToString(), IsRealmAdmin = true }]
                : [],
        };

        var imported = await applier.ImportNewRealmAsync(Manifest(withRole: false), ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        var clash = await applier.UpdateRealmAsync(Manifest(withRole: true), ct: ct);

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
        var imported = await applier.ImportNewRealmAsync(BuildGlobexManifest(slug, version: 1), ct);
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
        var updated = await applier.UpdateRealmAsync(BuildGlobexManifest(slug, version: 2), ct: ct);
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
        // Import a DISABLED confidential client (Enabled explicitly false).
        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = slug,
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
            },
            Apps = [new RealmManifestApp { Slug = "bp-app", DisplayName = "BP", Permissions = [new RealmManifestPermission("bp", "read")] }],
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "bp-web",
                    ClientType = "confidential",
                    RedirectUris = ["https://bp.test/cb1"],
                    Scopes = ["openid"],
                    AllowedGrantTypes = ["authorization_code", "refresh_token"],
                    Apps = ["bp-app"],
                    Enabled = false,
                },
            ],
        };
        Assert.False((await applier.ImportNewRealmAsync(manifest, ct)).IsError);

        // Apply a partial update: change the redirect URI, OMIT Enabled (null = no change).
        var patch = new RealmManifest
        {
            Realm = manifest.Realm,
            Clients =
            [
                new RealmManifestClient
                {
                    ClientId = "bp-web",
                    ClientType = "confidential",
                    RedirectUris = ["https://bp.test/cb2"],
                    Apps = ["bp-app"],
                    // Enabled deliberately omitted.
                },
            ],
        };
        Assert.False((await applier.UpdateRealmAsync(patch, ct: ct)).IsError);

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
        RealmManifestClient Client(string? accessTokenType) => new()
        {
            ClientId = "tt-web",
            ClientType = "confidential",
            RedirectUris = ["https://tt.test/cb"],
            Scopes = ["openid"],
            AllowedGrantTypes = ["authorization_code", "refresh_token"],
            AccessTokenType = accessTokenType,
        };
        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = slug,
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
            },
            Clients = [Client("Jwt")],
        };
        Assert.False((await applier.ImportNewRealmAsync(manifest, ct)).IsError);

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
        Assert.False((await applier.UpdateRealmAsync(manifest with { Clients = [Client(null)] }, ct: ct)).IsError);
        Assert.Equal(AccessTokenType.Jwt, await GetTokenTypeAsync());

        // Apply with an explicit 'Reference': the merge flips it back.
        Assert.False((await applier.UpdateRealmAsync(manifest with { Clients = [Client("Reference")] }, ct: ct)).IsError);
        Assert.Equal(AccessTokenType.Reference, await GetTokenTypeAsync());

        // An invalid value is a contextual validation error, not a silent default.
        var invalid = await applier.UpdateRealmAsync(manifest with { Clients = [Client("Bogus")] }, ct: ct);
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
        var result = await applier.UpdateRealmAsync(BuildGlobexManifest("ghost", version: 1), ct: ct);

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

        // Import a realm with keep-* + drop-* entities AND a full admin path
        // (realm-admin role + user + group). The prune manifest will OMIT every drop-*
        // entity AND the whole admin path — drop-* must go, the admin path must survive
        // (no lockout). drop-app is referenced by drop-role/drop-api/drop.read/drop-web,
        // all dropped too → exercises reverse-dependency-order pruning.
        var full = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Prune",
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "boot", Email = "boot@prune.test" },
            },
            Apps =
            [
                new RealmManifestApp { Slug = "keep-app", DisplayName = "Keep", Permissions = [new RealmManifestPermission("keep", "read")] },
                new RealmManifestApp { Slug = "drop-app", DisplayName = "Drop", Permissions = [new RealmManifestPermission("drop", "read")] },
            ],
            Apis =
            [
                new RealmManifestApi { Name = "keep-api", DisplayName = "Keep API", App = "keep-app" },
                new RealmManifestApi { Name = "drop-api", DisplayName = "Drop API", App = "drop-app" },
            ],
            Scopes =
            [
                new RealmManifestScope { Name = "keep.read", DisplayName = "Keep", App = "keep-app", Resources = ["keep-api"] },
                new RealmManifestScope { Name = "drop.read", DisplayName = "Drop", App = "drop-app", Resources = ["drop-api"] },
            ],
            Clients =
            [
                new RealmManifestClient { ClientId = "keep-web", ClientType = "confidential", RedirectUris = ["https://k.test/cb"], Scopes = ["openid"], AllowedGrantTypes = ["authorization_code"], Apps = ["keep-app"] },
                new RealmManifestClient { ClientId = "drop-web", ClientType = "confidential", RedirectUris = ["https://d.test/cb"], Scopes = ["openid"], AllowedGrantTypes = ["authorization_code"], Apps = ["drop-app"] },
            ],
            Roles =
            [
                new RealmManifestRole { Name = "keep-role", App = "keep-app", Permissions = [new RealmManifestPermission("keep", "read")] },
                new RealmManifestRole { Name = "drop-role", App = "drop-app", Permissions = [new RealmManifestPermission("drop", "read")] },
                new RealmManifestRole { Name = "super-admin", IsRealmAdmin = true },
            ],
            Users =
            [
                new RealmManifestUser { Key = "keepuser", Email = "keep@prune.test", UserName = "keepuser", Password = "Passw0rd!23" },
                new RealmManifestUser { Key = "dropuser", Email = "drop@prune.test", UserName = "dropuser", Password = "Passw0rd!23" },
                new RealmManifestUser { Key = "adminuser", Email = "admin2@prune.test", UserName = "adminuser", Password = "Passw0rd!23" },
            ],
            Groups =
            [
                new RealmManifestGroup { Name = "KeepGroup", Members = ["keepuser"], Roles = ["keep-role"] },
                new RealmManifestGroup { Name = "DropGroup", Members = ["dropuser"], Roles = ["drop-role"] },
                new RealmManifestGroup { Name = "AdminGroup", Members = ["adminuser"], Roles = ["super-admin"] },
            ],
        };
        var import = await applier.ImportNewRealmAsync(full, ct);
        Assert.False(import.IsError, import.IsError ? import.FirstError.Description : string.Empty);

        // The prune manifest keeps only the keep-* entities; everything else is absent.
        var keepOnly = new RealmManifest
        {
            Realm = full.Realm,
            Apps = [full.Apps[0]],
            Apis = [full.Apis[0]],
            Scopes = [full.Scopes[0]],
            Clients = [full.Clients[0]],
            Roles = [full.Roles[0]],
            Users = [full.Users[0]],
            Groups = [full.Groups[0]],
        };

        var pruned = await applier.UpdateRealmAsync(keepOnly, prune: true, deletions: null, ct);
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
    /// Builds the Globex manifest. <paramref name="version"/> 1 is the import baseline;
    /// version 2 changes every existing entity (display names, catalog, redirect, role
    /// permissions, user firstname, group membership) and adds a new "globex-viewer" role —
    /// exercising both the update and the upsert-create branch.
    /// </summary>
    /// <summary>
    /// Role names are unique per App, so the manifest keys roles <c>app/name</c>: an export
    /// with two "Author" roles round-trips without duplicating either, a group reference
    /// resolves the qualified key (and a bare name only while unambiguous), a patch that
    /// omits App still finds a uniquely-named role, and one app may not own two roles of
    /// the same name.
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
            Name = "Author", App = appSlug, Permissions = [new RealmManifestPermission("doc", "write")],
        };

        var manifest = new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug, DisplayName = "Authors", Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "boot", Email = "boot@authors.test" },
            },
            Apps = [AppOf("alpha"), AppOf("beta")],
            Roles = [Author("alpha"), Author("beta"), new RealmManifestRole { Name = "Reviewer", App = "beta", Permissions = [] }],
            Groups =
            [
                new RealmManifestGroup { Name = "Alpha writers", Roles = ["alpha/Author"] },
                new RealmManifestGroup { Name = "Beta writers", Roles = ["beta/Author"] },
                // A bare name is fine while exactly one role carries it.
                new RealmManifestGroup { Name = "Reviewers", Roles = ["Reviewer"] },
            ],
        };
        var imported = await applier.ImportNewRealmAsync(manifest, ct);
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
        var reapplied = await applier.UpdateRealmAsync(export.Value, ct: ct);
        Assert.False(reapplied.IsError, reapplied.IsError ? reapplied.FirstError.Description : string.Empty);
        Assert.Equal(before, await RoleIdsAsync());

        // A patch without App (absent = unchanged) updates a uniquely-named role...
        var patch = new RealmManifest
        {
            Realm = manifest.Realm,
            Roles = [new RealmManifestRole { Name = "Reviewer", Description = "Reviews drafts" }],
        };
        var patched = await applier.UpdateRealmAsync(patch, ct: ct);
        Assert.False(patched.IsError, patched.IsError ? patched.FirstError.Description : string.Empty);
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            Assert.Equal("Reviews drafts", (await session.LoadAsync<PermissionRole>(before.Reviewer, ct))!.Description);
        });

        // ...but is refused for an ambiguous one instead of silently picking the first.
        var ambiguous = await applier.UpdateRealmAsync(new RealmManifest
        {
            Realm = manifest.Realm,
            Roles = [new RealmManifestRole { Name = "Author", Description = "Which one?" }],
        }, ct: ct);
        Assert.True(ambiguous.IsError);
        Assert.Equal("Manifest.AmbiguousRole", ambiguous.FirstError.Code);

        // So is a group reference to the bare name.
        var ambiguousRef = await applier.UpdateRealmAsync(new RealmManifest
        {
            Realm = manifest.Realm,
            Groups = [new RealmManifestGroup { Name = "Anyone", Roles = ["Author"] }],
        }, ct: ct);
        Assert.True(ambiguousRef.IsError);
        Assert.Equal("Manifest.AmbiguousReference", ambiguousRef.FirstError.Code);

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
    /// Cross-references are <c>{ Key, Id }</c>: the Id wins when a live entity carries it
    /// (a stale key after a rename is harmless), the Key is the fallback when the id is
    /// unknown, and a bare string is always a key. The planner compares references by the
    /// entity they name, so a rename the reference follows by Id is not a change.
    /// </summary>
    [Fact]
    public async Task References_follow_the_id_and_fall_back_to_the_key()
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
        var imported = await applier.ImportNewRealmAsync(new RealmManifest
        {
            Realm = realm,
            Apps = [new RealmManifestApp { Slug = "alpha", DisplayName = "alpha", Permissions = [new RealmManifestPermission("doc", "write")] },
                    new RealmManifestApp { Slug = "beta", DisplayName = "beta", Permissions = [new RealmManifestPermission("doc", "write")] }],
            Roles = [new RealmManifestRole { Name = "Author", App = "alpha", Permissions = [] },
                     new RealmManifestRole { Name = "Author", App = "beta", Permissions = [] }],
            Users = [new RealmManifestUser { Key = "alice", Email = "alice@refs.test", UserName = "alice" }],
            Groups = [new RealmManifestGroup { Name = "Beta writers", Members = ["alice"], Roles = ["beta/Author"] }],
        }, ct);
        Assert.False(imported.IsError, imported.IsError ? imported.FirstError.Description : string.Empty);

        Guid alphaAuthor = default, betaAuthor = default, alice = default;
        await InTenantAsync(factory, slug, async sp =>
        {
            var session = sp.GetRequiredService<IDocumentSession>();
            var apps = (await session.Query<App>().Where(a => !a.IsDeleted).ToListAsync(ct)).ToDictionary(a => a.Slug, a => a.Id);
            var authors = await session.Query<PermissionRole>().Where(r => !r.IsDeleted && r.Name == "Author").ToListAsync(ct);
            alphaAuthor = authors.Single(r => r.AppId == apps["alpha"]).Id;
            betaAuthor = authors.Single(r => r.AppId == apps["beta"]).Id;
            alice = (await session.Query<Person>().SingleAsync(p => !p.IsDeleted && p.AccountName == "alice", ct)).Id;
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
        async Task ApplyGroupAsync(List<ManifestRef>? roles = null, List<ManifestRef>? members = null)
        {
            var result = await applier.UpdateRealmAsync(new RealmManifest
            {
                Realm = realm,
                Groups = [new RealmManifestGroup { Name = "Beta writers", Roles = roles, Members = members }],
            }, ct: ct);
            Assert.False(result.IsError, result.IsError ? result.FirstError.Description : string.Empty);
        }

        // A stale key next to a valid id: the id wins.
        await ApplyGroupAsync(roles: [new ManifestRef { Key = "beta/Renamed long ago", Id = new ShortGuid(betaAuthor).ToString() }]);
        Assert.Equal([betaAuthor], (await GroupAsync()).RoleIds);
        // An unknown id next to a valid key: the key is the fallback.
        await ApplyGroupAsync(roles: [new ManifestRef { Key = "alpha/Author", Id = new ShortGuid(Guid.NewGuid()).ToString() }]);
        Assert.Equal([alphaAuthor], (await GroupAsync()).RoleIds);
        // Id only.
        await ApplyGroupAsync(roles: [new ManifestRef { Id = betaAuthor.ToString() }]);
        Assert.Equal([betaAuthor], (await GroupAsync()).RoleIds);
        // Members follow the same rule.
        await ApplyGroupAsync(members: [new ManifestRef { Key = "nobody", Id = new ShortGuid(alice).ToString() }]);
        Assert.Equal([alice], (await GroupAsync()).MemberIds);
        // A bare string is ALWAYS a key — even when it happens to be an id.
        var idAsKey = await applier.UpdateRealmAsync(new RealmManifest
        {
            Realm = realm,
            Groups = [new RealmManifestGroup { Name = "Beta writers", Roles = [new ShortGuid(betaAuthor).ToString()] }],
        }, ct: ct);
        Assert.True(idAsKey.IsError);
        Assert.Equal("Manifest.UnknownReference", idAsKey.FirstError.Code);

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
        var plan = await planner.PlanAsync(exportBefore.Value, prune: false, baseline: exportBefore.Value, ct: ct);
        Assert.False(plan.IsError, plan.IsError ? plan.FirstError.Description : string.Empty);
        var groupEntry = plan.Value.Sections.Single(s => s.Name == "groups").Entries.Single(e => e.Key == "Beta writers");
        Assert.Equal("unchanged", groupEntry.Action);
        Assert.Empty(groupEntry.Conflicts);
        await ApplyGroupAsync(roles: exportBefore.Value.Groups.Single(g => g.Name == "Beta writers").Roles);
        Assert.Equal([betaAuthor], (await GroupAsync()).RoleIds);
        await InTenantAsync(factory, slug, async sp =>
            Assert.Equal("Writer", (await sp.GetRequiredService<IDocumentSession>().LoadAsync<PermissionRole>(betaAuthor, ct))!.Name));
    }

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
                App = "globex-app",
                Permissions = catalog.ToList(),
            },
        };
        if (v2)
            roles.Add(new RealmManifestRole
            {
                Name = "globex-viewer",
                App = "globex-app",
                Permissions = [new RealmManifestPermission("globex", "read")],
            });

        return new RealmManifest
        {
            Realm = new CreateRealmDto
            {
                Slug = slug,
                DisplayName = "Globex",
                Domains = [$"{slug}.localhost"],
                InitialAdmin = new InitialAdminDto { UserName = "admin", Email = $"admin@{slug}.test" },
            },
            Apps =
            [
                new RealmManifestApp
                {
                    Slug = "globex-app",
                    DisplayName = v2 ? "Globex App v2" : "Globex App",
                    Permissions = catalog,
                },
            ],
            Apis =
            [
                new RealmManifestApi
                {
                    Name = "globex-api",
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
                    Description = v2 ? "Updated admins" : "Admins",
                    Members = ["alice"],
                    Roles = v2 ? ["globex-admin", "globex-viewer"] : ["globex-admin"],
                },
            ],
        };
    }

    private static async Task InTenantAsync(
        ColdStartWebApplicationFactory factory, string slug, Func<IServiceProvider, Task> body)
    {
        using var _ = TenantContext.Enter(slug);
        using var scope = factory.Services.CreateScope();
        await body(scope.ServiceProvider);
    }
}
