using Modgud.Api.Tests.Infrastructure;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Services;
using Modgud.Permissions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// Federation v1 — Phase 4 (token/grant union, read side). Pins the
/// <see cref="IPermissionService"/> union overloads that combine durable
/// membership with the session-derived <c>sessionGroupIds</c> carried on the
/// grant:
/// <list type="bullet">
///   <item>a session group contributes its roles/permissions for the bound app;</item>
///   <item>a session group's ANCESTORS are inherited (a session child still
///   confers its parents' roles);</item>
///   <item>realm:admin is hard local-only — a session-sourced group never
///   confers it (even via an ancestor role), while a durable realm:admin is
///   kept (provenance-aware, not a blanket strip);</item>
///   <item>BoundTo still gates session groups;</item>
///   <item>an empty session set is byte-for-byte the no-arg behavior.</item>
/// </list>
/// The hub-boundary leak guard (the carrier never reaching a token) is pinned
/// separately as a unit test in <c>AuthorizationEndpointHelpersTests</c>.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class FederationV1Phase4Tests : IntegrationTestBase
{
    public FederationV1Phase4Tests(SharedPostgresFixture fixture) : base(fixture) { }

    private const string AppSlug = "phase4-app";

    [Fact]
    public async Task SessionGroup_Contributes_Roles_And_Permissions_For_Bound_App()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAppAsync(AppSlug, [("policy", "read"), ("policy", "write")]);

        var user = await Factory.CreateTestUserWithIdentityAsync("Sess", "Union", "SU", "sess-union@acme.com");
        var role = await Factory.CreateTestRoleAsync(
            $"R_{Guid.NewGuid():N}", [("policy", "write")], appSlug: AppSlug);
        // The user is NOT a durable member of this group — it only enters via the
        // session carrier.
        var sessionGroup = await Factory.CreateTestGroupAsync(
            $"SG_{Guid.NewGuid():N}", memberIds: [], roleIds: [role.Id], boundTo: [AppSlug]);

        using var s = Factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IPermissionService>();

        // Without the carrier: durable membership is empty → nothing.
        var durableOnly = await svc.GetUserPermissionsAsync(user.Id, AppSlug, [], ct);
        Assert.Empty(durableOnly);

        // With the carrier: the session group's permission is unioned in.
        var withSession = await svc.GetUserPermissionsAsync(user.Id, AppSlug, [sessionGroup.Id], ct);
        Assert.Contains("policy:write", withSession);
        Assert.DoesNotContain("policy:read", withSession);

        // Roles overload picks the same group up.
        var roles = await svc.GetUserRolesAsync(user.Id, AppSlug, [sessionGroup.Id], ct);
        Assert.Contains(role.Id, roles.Select(r => r.Id));

        // Groups overload includes the session group itself.
        var groups = await svc.GetUserGroupsAsync(user.Id, [sessionGroup.Id], ct);
        Assert.Contains(sessionGroup.Id, groups.Select(g => g.Id));
    }

    [Fact]
    public async Task SessionGroup_Ancestor_Roles_Are_Inherited()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAppAsync(AppSlug, [("policy", "read"), ("policy", "write")]);

        var user = await Factory.CreateTestUserWithIdentityAsync("Sess", "Ancestor", "SA", "sess-anc@acme.com");

        var parentRole = await Factory.CreateTestRoleAsync(
            $"RP_{Guid.NewGuid():N}", [("policy", "read")], appSlug: AppSlug);
        var childRole = await Factory.CreateTestRoleAsync(
            $"RC_{Guid.NewGuid():N}", [("policy", "write")], appSlug: AppSlug);

        // child is a MEMBER of parent → parent is the child's ancestor; roles flow
        // up the member-of graph. Create the child first so the parent can list it.
        var child = await Factory.CreateTestGroupAsync(
            $"Child_{Guid.NewGuid():N}", memberIds: [], roleIds: [childRole.Id], boundTo: [AppSlug]);
        var parent = await Factory.CreateTestGroupAsync(
            $"Parent_{Guid.NewGuid():N}", memberIds: [child.Id], roleIds: [parentRole.Id], boundTo: [AppSlug]);

        using var s = Factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IPermissionService>();

        // Session-place the user into the CHILD only — they must still inherit the
        // PARENT's role through the ancestor walk.
        var permissions = await svc.GetUserPermissionsAsync(user.Id, AppSlug, [child.Id], ct);
        Assert.Contains("policy:write", permissions); // direct (child role)
        Assert.Contains("policy:read", permissions);  // inherited (parent role)

        var groups = await svc.GetUserGroupsAsync(user.Id, [child.Id], ct);
        Assert.Contains(child.Id, groups.Select(g => g.Id));
        Assert.Contains(parent.Id, groups.Select(g => g.Id));
    }

    [Fact]
    public async Task SessionGroup_Cannot_Confer_RealmAdmin_But_Durable_Is_Kept()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAppAsync(AppSlug, [("policy", "read")]);

        var sessionUser = await Factory.CreateTestUserWithIdentityAsync("Sess", "Rogue", "SR", "sess-rogue@acme.com");
        var durableUser = await Factory.CreateTestUserWithIdentityAsync("Dur", "Admin", "DA", "dur-admin@acme.com");

        var realmAdminRole = await Factory.CreateTestRoleAsync(
            $"RA_{Guid.NewGuid():N}", isRealmAdmin: true);
        var readRole = await Factory.CreateTestRoleAsync(
            $"RR_{Guid.NewGuid():N}", [("policy", "read")], appSlug: AppSlug);

        // A non-realm-admin child group; its ancestor confers realm:admin. This is
        // exactly the case the write-time config guard can't see (the child itself
        // is harmless; the danger is the inherited parent role).
        var child = await Factory.CreateTestGroupAsync(
            $"RogueChild_{Guid.NewGuid():N}", memberIds: [], roleIds: [readRole.Id], boundTo: [AppSlug]);
        // The realm-admin parent: durableUser is a DIRECT member; child is a member
        // (so the parent is the child's ancestor). Wildcard-bound like real
        // realm-admin groups.
        await Factory.CreateTestGroupAsync(
            $"RealmAdmins_{Guid.NewGuid():N}",
            memberIds: [durableUser.Id, child.Id], roleIds: [realmAdminRole.Id], boundTo: ["*"]);

        using var s = Factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IPermissionService>();

        // Session path: realm:admin reached ONLY via the session carrier → stripped.
        // The non-privileged inherited permission still comes through.
        var sessionPerms = await svc.GetUserPermissionsAsync(sessionUser.Id, AppSlug, [child.Id], ct);
        Assert.DoesNotContain(PermissionEvaluator.RealmAdminPermission, sessionPerms);
        Assert.Contains("policy:read", sessionPerms);

        // The realm-admin role itself must not leak into the roles emission either.
        var sessionRoles = await svc.GetUserRolesAsync(sessionUser.Id, AppSlug, [child.Id], ct);
        Assert.DoesNotContain(realmAdminRole.Id, sessionRoles.Select(r => r.Id));

        // Durable path: a genuinely-held realm:admin is kept (the strip is
        // provenance-aware, not a blanket removal).
        var durablePerms = await svc.GetUserPermissionsAsync(durableUser.Id, AppSlug, [], ct);
        Assert.Contains(PermissionEvaluator.RealmAdminPermission, durablePerms);
    }

    [Fact]
    public async Task SessionGroup_Not_Bound_To_App_Contributes_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAppAsync(AppSlug, [("policy", "write")]);

        var user = await Factory.CreateTestUserWithIdentityAsync("Sess", "Bound", "SB", "sess-bound@acme.com");
        var role = await Factory.CreateTestRoleAsync(
            $"RB_{Guid.NewGuid():N}", [("policy", "write")], appSlug: AppSlug);
        // Bound to a DIFFERENT app — dormant for phase4-app.
        var sessionGroup = await Factory.CreateTestGroupAsync(
            $"SGother_{Guid.NewGuid():N}", memberIds: [], roleIds: [role.Id], boundTo: ["some-other-app"]);

        using var s = Factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IPermissionService>();

        var permissions = await svc.GetUserPermissionsAsync(user.Id, AppSlug, [sessionGroup.Id], ct);
        Assert.Empty(permissions);
    }

    [Fact]
    public async Task Empty_SessionGroups_Overload_Equals_NoArg()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateAppAsync(AppSlug, [("policy", "read"), ("policy", "write")]);

        var user = await Factory.CreateTestUserWithIdentityAsync("Dur", "Parity", "DP", "dur-parity@acme.com");
        var role = await Factory.CreateTestRoleAsync(
            $"RD_{Guid.NewGuid():N}", [("policy", "write")], appSlug: AppSlug);
        await Factory.CreateTestGroupAsync(
            $"GD_{Guid.NewGuid():N}", memberIds: [user.Id], roleIds: [role.Id], boundTo: [AppSlug]);

        using var s = Factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IPermissionService>();

        var permsNoArg = (await svc.GetUserPermissionsAsync(user.Id, AppSlug, ct)).OrderBy(x => x).ToList();
        var permsEmpty = (await svc.GetUserPermissionsAsync(user.Id, AppSlug, [], ct)).OrderBy(x => x).ToList();
        Assert.Equal(permsNoArg, permsEmpty);

        var rolesNoArg = (await svc.GetUserRolesAsync(user.Id, AppSlug, ct)).Select(r => r.Id).OrderBy(x => x).ToList();
        var rolesEmpty = (await svc.GetUserRolesAsync(user.Id, AppSlug, [], ct)).Select(r => r.Id).OrderBy(x => x).ToList();
        Assert.Equal(rolesNoArg, rolesEmpty);

        var groupsNoArg = (await svc.GetUserGroupsAsync(user.Id, ct)).Select(g => g.Id).OrderBy(x => x).ToList();
        var groupsEmpty = (await svc.GetUserGroupsAsync(user.Id, [], ct)).Select(g => g.Id).OrderBy(x => x).ToList();
        Assert.Equal(groupsNoArg, groupsEmpty);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task CreateAppAsync(string slug, IReadOnlyList<(string Resource, string Action)> permissions)
    {
        using var scope = Factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var perms = permissions
            .Select(p => new AppPermission(Guid.NewGuid(), p.Resource, p.Action, Description: null))
            .ToList();
        var appId = Guid.NewGuid();
        session.Events.StartStream<App>(appId, new AppCreatedEvent(
            Id: appId, Slug: slug, DisplayName: slug, Description: null,
            Permissions: perms, IsSystem: false));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
