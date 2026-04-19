using Cocoar.Auth.Domain.Authorization;

namespace Cocoar.Auth.Application.Authorization;

/// <summary>
/// Resolves permissions for a principal by walking the Group → Role → Permission graph.
/// All membership lookups follow the transitive nested-group structure with cycle
/// protection. There is no direct user→role or user→permission grant — permissions
/// flow exclusively through group membership.
/// </summary>
public interface IPermissionService
{
    Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken ct = default);
    Task<List<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all groups a principal belongs to — both direct memberships and
    /// transitively-inherited memberships via nested groups. If principal X is a
    /// direct member of group A, and A is a member of group B, X's effective groups
    /// are {A, B} — A and all ancestors of A.
    /// </summary>
    Task<List<AuthorizationGroup>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default);
    Task<List<PermissionRole>> GetUserRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all descendant group ids of the given group (transitive MemberIds that
    /// reference other groups). Used for cycle detection and propagation logic.
    /// </summary>
    Task<HashSet<Guid>> GetDescendantGroupIdsAsync(Guid groupId, CancellationToken ct = default);
}
