using System.Net;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// End-to-end integration tests for the permission gate at HTTP level.
/// Drives the full pipeline: cookie auth → <c>RealmMiddleware</c> →
/// <c>PermissionEndpointFilter</c> → <c>PermissionService</c>.
///
/// <para>The gate exercised is <c>GET /api/user</c>, which requires
/// <c>user:read</c> within the <c>modgud</c> App. The test users start
/// from no group and have a custom role+group built per case so we can
/// vary <see cref="Group.BoundTo"/> and the role's App-link independently.
/// This pins the BFS resolution + BoundTo filter + role-AppId filter +
/// bypass cascade against the real Marten store — the unit tests cover
/// individual pieces, this file proves they compose.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PermissionResolutionTests : IntegrationTestBase
{
    public PermissionResolutionTests(SharedPostgresFixture fixture) : base(fixture) { }

    [Fact]
    public async Task User_WithNoGroup_Returns_403_On_UserRead()
    {
        await Factory.CreateTestUserWithIdentityAsync(
            firstname: "No", lastname: "Group", acronym: "NG",
            email: "ng@test.com", password: "TestPass1234");
        var client = await CreateAuthenticatedClientAsync("ng", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task User_With_UserRead_In_BoundToModgud_Group_Returns_200()
    {
        // Catalog grant "user:read" + role.AppId=modgud's id +
        // group.BoundTo=[modgud] → group active for modgud and
        // role contributes its catalog grant → match.
        var user = await CreateUserAsync("ok", "modgud-user-read");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.Modgud, permissions: [("user", "read")],
            groupBoundTo: [AppSlugs.Modgud]);
        var client = await CreateAuthenticatedClientAsync("ok", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_UserRead_In_DormantGroup_Returns_403()
    {
        // Same role assignment as the previous test, but group.BoundTo=[]
        // (dormant) — the group does not contribute to permission resolution
        // for any app. Pins that BoundTo is the activation switch, not a
        // hint.
        var user = await CreateUserAsync("dr", "dormant-group");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.Modgud, permissions: [("user", "read")],
            groupBoundTo: []);
        var client = await CreateAuthenticatedClientAsync("dr", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task User_With_WildcardBoundTo_Returns_200()
    {
        // BoundTo=["*"] = active in every app, regardless of which app the
        // permission resolution targets. Used by the realm-admin group.
        var user = await CreateUserAsync("ww", "wildcard");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.Modgud, permissions: [("user", "read")],
            groupBoundTo: ["*"]);
        var client = await CreateAuthenticatedClientAsync("ww", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_WrongAppBoundTo_Returns_403()
    {
        // Role grants user:read on modgud, but the group is only
        // active in "acme-tasks". Permission resolution for modgud
        // ignores the group → no permission. Pins cross-app group isolation.
        // (acme-tasks is a fictional placeholder app slug used in tests.)
        var user = await CreateUserAsync("wa", "wrong-app");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.Modgud, permissions: [("user", "read")],
            groupBoundTo: ["acme-tasks"]);
        var client = await CreateAuthenticatedClientAsync("wa", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task User_With_ResourceAdmin_In_SameApp_Returns_200()
    {
        // user:admin bypass: grants every action on the user resource within
        // modgud, including read. Pins PermissionEvaluator's
        // resource-admin tier.
        //
        // The seeded modgud catalog has user:read/write but not
        // user:admin (admin is in app/login-provider/oauth/gdpr but not on
        // user). Add the entry first so the role-grant FK resolves.
        await AddCatalogEntryAsync(AppSlugs.Modgud, "user", "admin");
        var user = await CreateUserAsync("ra", "resource-admin");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.Modgud, permissions: [("user", "admin")],
            groupBoundTo: [AppSlugs.Modgud]);
        var client = await CreateAuthenticatedClientAsync("ra", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_RealmAdmin_Returns_200_Even_With_DifferentAppBoundTo()
    {
        // realm:admin is the realm-wide bypass — flagged via the
        // PermissionRole.IsRealmAdmin bit, NOT a catalog FK. Bypass works
        // regardless of which app the gate is resolving for, as long as the
        // user's group is active there (or wildcard).
        var user = await CreateUserAsync("re", "realm-admin");
        await GrantRealmAdminAsync(user.Id, groupBoundTo: ["*"]);
        var client = await CreateAuthenticatedClientAsync("re", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_CrossAppRole_Does_Not_Leak_Returns_403()
    {
        // Role with a different App's catalog grant cannot grant
        // permissions in modgud even when the parent group is
        // active in modgud (BoundTo=["*"]). Pins the role-AppId
        // filter inside GetUserPermissionsAsync.
        //
        // Implementation note: the acme-tasks App must be seeded for
        // CreateTestRoleAsync to FK against its catalog. The realm
        // doesn't ship with an acme-tasks App by default, so the test
        // creates one with a single user:read entry and grants from it.
        var user = await CreateUserAsync("xa", "cross-app");
        await CreateMinimalAppAsync("acme-tasks", "Acme Tasks", catalog: [("user", "read")]);
        await GrantAsync(user.Id,
            roleAppSlug: "acme-tasks", permissions: [("user", "read")],
            groupBoundTo: ["*"]);
        var client = await CreateAuthenticatedClientAsync("xa", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a login-capable test user with no permissions. Subsequent
    /// <see cref="GrantAsync"/> attaches a role + group.
    /// </summary>
    private Task<Modgud.Infrastructure.Persistence.Marten.Projections.Users.UserView> CreateUserAsync(
        string acronym, string nameSuffix) =>
        Factory.CreateTestUserWithIdentityAsync(
            firstname: $"P_{nameSuffix}", lastname: $"L_{nameSuffix}", acronym: acronym,
            email: $"{acronym}@test.com", password: "TestPass1234");

    private async Task GrantAsync(
        Guid userId, string roleAppSlug,
        IReadOnlyList<(string Resource, string Action)> permissions,
        IReadOnlyList<string> groupBoundTo)
    {
        var role = await Factory.CreateTestRoleAsync(
            name: $"R_{Guid.NewGuid():N}",
            permissions: permissions,
            appSlug: roleAppSlug);
        await Factory.CreateTestGroupAsync(
            name: $"G_{Guid.NewGuid():N}",
            memberIds: [userId],
            roleIds: [role.Id],
            boundTo: groupBoundTo.ToList());
    }

    private async Task GrantRealmAdminAsync(Guid userId, IReadOnlyList<string> groupBoundTo)
    {
        var role = await Factory.CreateTestRoleAsync(
            name: $"RealmAdmin_{Guid.NewGuid():N}",
            isRealmAdmin: true);
        await Factory.CreateTestGroupAsync(
            name: $"G_{Guid.NewGuid():N}",
            memberIds: [userId],
            roleIds: [role.Id],
            boundTo: groupBoundTo.ToList());
    }

    /// <summary>
    /// Adds a (resource, action) entry to an existing App's catalog so a
    /// later <see cref="GrantAsync"/> can FK into it. Used by tests that
    /// need a catalog entry not in the default seed (e.g. user:admin).
    /// </summary>
    private async Task AddCatalogEntryAsync(string appSlug, string resource, string action)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IDocumentSession>();

        var app = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == appSlug && !a.IsDeleted)
            ?? throw new InvalidOperationException($"App '{appSlug}' not found.");

        if (app.Permissions.Any(p => p.Resource == resource && p.Action == action))
            return;

        var newPerms = new List<AppPermission>(app.Permissions)
        {
            new(Guid.NewGuid(), resource, action, Description: null),
        };

        session.Events.Append(app.Id, new Modgud.Authorization.Events.AppUpdatedEvent(
            Id: app.Id,
            DisplayName: app.DisplayName,
            Description: app.Description,
            Permissions: newPerms));
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a non-system App with a minimal catalog so cross-app tests can
    /// build roles against it.
    /// </summary>
    private async Task CreateMinimalAppAsync(
        string slug, string displayName,
        IReadOnlyList<(string Resource, string Action)> catalog)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IDocumentSession>();

        var existing = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == slug && !a.IsDeleted);
        if (existing is not null) return;

        var perms = catalog
            .Select(c => new AppPermission(Guid.NewGuid(), c.Resource, c.Action, Description: null))
            .ToList();
        var id = Guid.NewGuid();
        session.Events.StartStream<App>(id, new Modgud.Authorization.Events.AppCreatedEvent(
            Id: id,
            Slug: slug,
            DisplayName: displayName,
            Description: null,
            Permissions: perms,
            IsSystem: false));
        await session.SaveChangesAsync();
    }
}
