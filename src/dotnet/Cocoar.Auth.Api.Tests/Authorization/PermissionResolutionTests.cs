using System.Net;
using Cocoar.Auth.Api.Tests.Infrastructure;
using Cocoar.Auth.Authorization.Apps;

namespace Cocoar.Auth.Api.Tests.Authorization;

/// <summary>
/// End-to-end integration tests for the permission gate at HTTP level.
/// Drives the full pipeline: cookie auth → <c>RealmMiddleware</c> →
/// <c>PermissionEndpointFilter</c> → <c>PermissionService</c>.
///
/// <para>The gate exercised is <c>GET /api/user</c>, which requires
/// <c>cocoar-auth:user:read</c>. The test users start from
/// <c>permissions: []</c> (no group) and have a custom role+group built per
/// case so we can vary <see cref="Group.BoundTo"/> and <see cref="PermissionRole.AppSlug"/>
/// independently. This pins the BFS resolution + BoundTo filter +
/// role-AppSlug filter + bypass cascade against the real Marten store —
/// the unit tests cover individual pieces, this file proves they compose.</para>
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
            email: "ng@test.com", password: "TestPass1234",
            permissions: []);
        var client = await CreateAuthenticatedClientAsync("ng", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task User_With_BareReadAction_In_BoundToCocoarAuth_Group_Returns_200()
    {
        // Bare action "read" + role.AppSlug=cocoar-auth + group.BoundTo=[cocoar-auth]
        // → expands to "cocoar-auth:user:read", group is active → match.
        var user = await CreateUserAsync("ok", "cocoar-auth-user-read");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.CocoarAuth, resourceType: "user", actions: ["read"],
            groupBoundTo: [AppSlugs.CocoarAuth]);
        var client = await CreateAuthenticatedClientAsync("ok", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_BareReadAction_In_DormantGroup_Returns_403()
    {
        // Same role assignment as the previous test, but group.BoundTo=[]
        // (dormant) — the group does not contribute to permission resolution
        // for any app. Pins that BoundTo is the activation switch, not a
        // hint.
        var user = await CreateUserAsync("dr", "dormant-group");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.CocoarAuth, resourceType: "user", actions: ["read"],
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
            roleAppSlug: AppSlugs.CocoarAuth, resourceType: "user", actions: ["read"],
            groupBoundTo: ["*"]);
        var client = await CreateAuthenticatedClientAsync("ww", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_WrongAppBoundTo_Returns_403()
    {
        // Role grants cocoar-auth:user:read, but the group is only active in
        // "timetodo". Permission resolution for cocoar-auth ignores the
        // group → no permission. Pins cross-app group isolation.
        var user = await CreateUserAsync("wa", "wrong-app");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.CocoarAuth, resourceType: "user", actions: ["read"],
            groupBoundTo: ["timetodo"]);
        var client = await CreateAuthenticatedClientAsync("wa", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task User_With_ResourceAdmin_In_SameApp_Returns_200()
    {
        // cocoar-auth:user:admin bypass: grants every action on the user
        // resource within cocoar-auth, including read. Pins
        // PermissionEvaluator's resource-admin tier.
        var user = await CreateUserAsync("ra", "resource-admin");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.CocoarAuth, resourceType: "user", actions: ["admin"],
            groupBoundTo: [AppSlugs.CocoarAuth]);
        var client = await CreateAuthenticatedClientAsync("ra", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_AppAdmin_Returns_200()
    {
        // cocoar-auth:admin bypass: every resource within cocoar-auth.
        // Stored as the fully-qualified "cocoar-auth:admin" so it survives
        // the bare-action expansion path.
        var user = await CreateUserAsync("aa", "app-admin");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.CocoarAuth, resourceType: "app",
            actions: ["cocoar-auth:admin"],
            groupBoundTo: [AppSlugs.CocoarAuth]);
        var client = await CreateAuthenticatedClientAsync("aa", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_RealmAdmin_Returns_200_Even_With_TimetodoBoundTo()
    {
        // realm:admin is the realm-wide bypass. It is fully-qualified so it
        // survives the expansion path; combined with BoundTo=[*] it makes
        // the System Admin user — but the bypass works even from a more
        // restrictive group as long as the permission lands in the user's
        // grant set for the requested app. Wildcard BoundTo here.
        var user = await CreateUserAsync("re", "realm-admin");
        await GrantAsync(user.Id,
            roleAppSlug: AppSlugs.CocoarAuth, resourceType: "app",
            actions: ["realm:admin"],
            groupBoundTo: ["*"]);
        var client = await CreateAuthenticatedClientAsync("re", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task User_With_CrossAppRole_Does_Not_Leak_Returns_403()
    {
        // Role with AppSlug=timetodo + bare action "read" on resource "user"
        // expands to "timetodo:user:read", NOT "cocoar-auth:user:read".
        // Even though the user's group is active in cocoar-auth (BoundTo
        // contains "*"), the role itself belongs to a different app and
        // cannot grant cocoar-auth permissions. Pins the role-AppSlug filter
        // inside GetUserPermissionsAsync.
        var user = await CreateUserAsync("xa", "cross-app");
        await GrantAsync(user.Id,
            roleAppSlug: "timetodo", resourceType: "user", actions: ["read"],
            groupBoundTo: ["*"]);
        var client = await CreateAuthenticatedClientAsync("xa", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task User_With_AppAdmin_For_OtherApp_Does_Not_Leak_Returns_403()
    {
        // timetodo:admin grants every resource in timetodo — but cocoar-auth
        // resolution must not see it as a cocoar-auth bypass.
        var user = await CreateUserAsync("oa", "other-app-admin");
        await GrantAsync(user.Id,
            roleAppSlug: "timetodo", resourceType: "app",
            actions: ["timetodo:admin"],
            groupBoundTo: ["*"]);
        var client = await CreateAuthenticatedClientAsync("oa", "TestPass1234");

        var r = await client.GetAsync("/api/user", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a login-capable test user with no permissions. Subsequent
    /// <see cref="GrantAsync"/> attaches a role + group.
    /// </summary>
    private Task<Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users.UserView> CreateUserAsync(
        string acronym, string nameSuffix) =>
        Factory.CreateTestUserWithIdentityAsync(
            firstname: $"P_{nameSuffix}", lastname: $"L_{nameSuffix}", acronym: acronym,
            email: $"{acronym}@test.com", password: "TestPass1234", permissions: []);

    /// <summary>
    /// Attaches a fresh role + group to the user. The role belongs to
    /// <paramref name="roleAppSlug"/>, scopes its actions to
    /// <paramref name="resourceType"/>, and carries the strings in
    /// <paramref name="actions"/>. The group lists <paramref name="groupBoundTo"/>
    /// in BoundTo and the user as its only member.
    /// </summary>
    private async Task GrantAsync(
        Guid userId, string roleAppSlug, string resourceType,
        IReadOnlyList<string> actions, IReadOnlyList<string> groupBoundTo)
    {
        var role = await Factory.CreateTestRoleAsync(
            name: $"R_{Guid.NewGuid():N}",
            resourceType: resourceType,
            permissions: actions.ToList(),
            appSlug: roleAppSlug);
        await Factory.CreateTestGroupAsync(
            name: $"G_{Guid.NewGuid():N}",
            memberIds: [userId],
            roleIds: [role.Id],
            boundTo: groupBoundTo.ToList());
    }
}
