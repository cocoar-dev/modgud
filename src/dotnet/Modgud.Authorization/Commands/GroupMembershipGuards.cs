using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Authorization.Services;
using ErrorOr;
using Marten;

namespace Modgud.Authorization.Commands;

/// <summary>
/// Shared write-time guards for group commands. Keeps the federation v1
/// <c>realm:admin</c>-local-only invariant AND the "only a realm:admin may
/// confer realm:admin" privilege-escalation guard in one place so create and
/// update enforce them identically.
/// </summary>
internal static class GroupMembershipGuards
{
    /// <summary>
    /// Federation v1 (decision G): a group whose roles confer <c>realm:admin</c>
    /// can never be marked <see cref="Principals.Group.ExternallyDrivable"/>.
    /// Returns a validation error if any of <paramref name="roleIds"/> belongs to
    /// a role with <c>IsRealmAdmin</c>; otherwise <c>null</c>.
    /// </summary>
    public static async Task<Error?> RejectIfConfersRealmAdminAsync(
        IDocumentSession session,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken ct)
    {
        if (await AnyRoleConfersRealmAdminAsync(session, roleIds, ct))
            return Error.Validation("Group.ExternallyDrivableRealmAdmin",
                "A group that confers realm:admin cannot be externally drivable — external claims are untrusted input.");

        return null;
    }

    /// <summary>
    /// True if any of <paramref name="roleIds"/> resolves to a non-deleted role
    /// with <c>IsRealmAdmin</c>. The single-query primitive both the federation
    /// guard and the privilege-escalation guard build on.
    /// </summary>
    public static async Task<bool> AnyRoleConfersRealmAdminAsync(
        IDocumentSession session,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken ct)
    {
        if (roleIds.Count == 0) return false;

        var ids = roleIds.Distinct().ToList();
        return await session.Query<PermissionRole>()
            .Where(r => ids.Contains(r.Id) && r.IsRealmAdmin && !r.IsDeleted)
            .AnyAsync(ct);
    }

    /// <summary>
    /// True if <paramref name="group"/> confers <c>realm:admin</c> on its
    /// members — either directly through its own <see cref="Group.RoleIds"/> or
    /// transitively through an ancestor group it is a member of. A principal
    /// added to such a group (or any of its descendants) inherits realm:admin,
    /// so changing its membership is itself a conferral and must be gated on the
    /// caller already holding realm:admin.
    /// </summary>
    public static async Task<bool> GroupConfersRealmAdminAsync(
        IDocumentSession session,
        IPermissionService permissionService,
        Group group,
        CancellationToken ct)
    {
        // Ancestors = the groups this group is (transitively) a member of.
        // realm:admin flows DOWN to descendants, so a member of `group` also
        // inherits the roles of every ancestor.
        var ancestors = await permissionService.GetUserGroupsAsync(group.Id, ct);
        var roleIds = group.RoleIds
            .Concat(ancestors.SelectMany(g => g.RoleIds))
            .ToList();

        return await AnyRoleConfersRealmAdminAsync(session, roleIds, ct);
    }
}
