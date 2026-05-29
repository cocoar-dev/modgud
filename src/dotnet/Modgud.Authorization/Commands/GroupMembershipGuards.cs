using Modgud.Authorization.Roles;
using ErrorOr;
using Marten;

namespace Modgud.Authorization.Commands;

/// <summary>
/// Shared write-time guards for group commands. Keeps the federation v1
/// <c>realm:admin</c>-local-only invariant in one place so create and update
/// enforce it identically.
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
        if (roleIds.Count == 0) return null;

        var ids = roleIds.ToList();
        var confersRealmAdmin = await session.Query<PermissionRole>()
            .Where(r => ids.Contains(r.Id) && r.IsRealmAdmin)
            .AnyAsync(ct);

        if (confersRealmAdmin)
            return Error.Validation("Group.ExternallyDrivableRealmAdmin",
                "A group that confers realm:admin cannot be externally drivable — external claims are untrusted input.");

        return null;
    }
}
