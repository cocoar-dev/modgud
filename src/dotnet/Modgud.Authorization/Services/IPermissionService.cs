using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;

namespace Modgud.Authorization.Services;

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

    // ── Federation v1 union overloads (decision D) ────────────────────────
    // These add the live-session, externally-derived group set on top of the
    // durable membership. <paramref name="sessionGroupIds"/> are the
    // ExternallyDrivable group IDs matched at login (carried on the sign-in
    // cookie → OpenIddict grant as the no-destination "modgud:session-group"
    // claim) and re-discovered at token/UserInfo time. They are unioned with
    // the durable BFS result, their ancestors walked too (a session child still
    // confers its parents' roles), and tagged with provenance so a
    // session-sourced group can NEVER confer realm:admin (hard local-only,
    // decision G). These are deliberately distinct OVERLOADS — not optional
    // params on the methods above — so the non-OAuth call sites stay unchanged
    // and the one union call site (BuildResourceAccessAsync) is greppable (I8).

    /// <summary>
    /// As <see cref="GetUserGroupsAsync(Guid, CancellationToken)"/>, plus the
    /// session-derived <paramref name="sessionGroupIds"/> and their ancestors.
    /// </summary>
    Task<List<Group>> GetUserGroupsAsync(
        Guid userId, IReadOnlyCollection<Guid> sessionGroupIds, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="GetUserPermissionsAsync(Guid, string, CancellationToken)"/>,
    /// unioning the session-derived <paramref name="sessionGroupIds"/> (and their
    /// ancestors). The synthetic <c>realm:admin</c> entry is added ONLY for roles
    /// reached through a durable (source=local) group — never from a session
    /// source. <c>&lt;app&gt;:admin</c> and below may be externally driven and are
    /// emitted regardless of provenance.
    /// </summary>
    Task<List<string>> GetUserPermissionsAsync(
        Guid userId, string appSlug, IReadOnlyCollection<Guid> sessionGroupIds, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="GetUserRolesAsync(Guid, string, CancellationToken)"/>,
    /// unioning the session-derived <paramref name="sessionGroupIds"/> (and their
    /// ancestors). A realm-admin role is returned ONLY when reached durably;
    /// app-scoped roles are provenance-agnostic.
    /// </summary>
    Task<List<PermissionRole>> GetUserRolesAsync(
        Guid userId, string appSlug, IReadOnlyCollection<Guid> sessionGroupIds, CancellationToken ct = default);
}
