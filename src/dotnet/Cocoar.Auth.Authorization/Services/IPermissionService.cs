using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;

namespace Cocoar.Auth.Authorization.Services;

/// <summary>
/// Resolves authorization for a user within an app. Permissions are always
/// scoped: the caller declares which app the request is for, and only roles
/// from groups active in that app contribute to the result.
///
/// <para>BoundTo wildcard <c>"*"</c> on a group means "active in every app" —
/// used for the realm-admin group seeded at setup so a system admin can
/// govern any app.</para>
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Checks whether <paramref name="userId"/> may perform
    /// <paramref name="permission"/> within <paramref name="appSlug"/>.
    /// </summary>
    Task<bool> HasPermissionAsync(Guid userId, string appSlug, string permission, CancellationToken ct = default);

    /// <summary>
    /// Returns every permission string the user holds in
    /// <paramref name="appSlug"/>: bare 2-segment <c>"resource:action"</c>
    /// strings expanded from the catalog FK references on each role's
    /// <c>PermissionIds</c>, plus the synthetic <c>"realm:admin"</c> entry
    /// when any reachable role carries <c>IsRealmAdmin=true</c>.
    /// </summary>
    Task<List<string>> GetUserPermissionsAsync(Guid userId, string appSlug, CancellationToken ct = default);

    /// <summary>
    /// Returns every group the principal belongs to — direct memberships plus
    /// all ancestor groups reached via nested-group traversal. Realm-wide; not
    /// filtered by BoundTo. Callers that need app-specific membership filter
    /// the result themselves.
    /// </summary>
    Task<List<Group>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the user's roles applicable to <paramref name="appSlug"/> — only
    /// roles whose <see cref="PermissionRole.AppId"/> resolves to the requested
    /// App or whose <see cref="PermissionRole.IsRealmAdmin"/> flag is set, AND
    /// whose owning group is active in that app.
    /// </summary>
    Task<List<PermissionRole>> GetUserRolesAsync(Guid userId, string appSlug, CancellationToken ct = default);

    /// <summary>
    /// Returns every descendant group id of the given group (transitive member
    /// ids that reference other groups). Used for cycle detection and bulk
    /// propagation logic.
    /// </summary>
    Task<HashSet<Guid>> GetDescendantGroupIdsAsync(Guid groupId, CancellationToken ct = default);
}
