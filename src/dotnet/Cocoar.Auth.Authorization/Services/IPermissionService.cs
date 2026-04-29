using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;

namespace Cocoar.Auth.Authorization.Services;

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken ct = default);

    Task<List<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every group the principal belongs to — direct memberships plus
    /// all ancestor groups reached via nested-group traversal. If principal X is
    /// a direct member of group A and A is a member of B, the effective groups
    /// are {A, B}.
    /// </summary>
    Task<List<Group>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default);

    Task<List<PermissionRole>> GetUserRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns every descendant group id of the given group (transitive member
    /// ids that reference other groups). Used for cycle detection and bulk
    /// propagation logic.
    /// </summary>
    Task<HashSet<Guid>> GetDescendantGroupIdsAsync(Guid groupId, CancellationToken ct = default);
}
