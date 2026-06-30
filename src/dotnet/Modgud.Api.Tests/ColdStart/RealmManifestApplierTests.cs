using Modgud.Api.Features.Admin.Provisioning;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Application.DTOs.OAuth;
using Modgud.Application.DTOs.Realms;
using Modgud.Application.Services;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Domain.OAuth.Apis;
using Modgud.Domain.OAuth.Applications;
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

        var pruned = await applier.UpdateRealmAsync(keepOnly, prune: true, ct);
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
