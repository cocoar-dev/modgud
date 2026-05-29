using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Authorization.Roles;
using Modgud.Permissions;
using Marten;

namespace Modgud.Authorization.Services;

/// <summary>
/// Default permission service. Resolves permissions via
/// Principal → Group → Role → Permission, with transitive group traversal,
/// scoped to a single app.
/// <para>
/// BFS loads every non-deleted <see cref="Group"/> in one query and walks the
/// member-of graph in memory. For typical tenant sizes (hundreds of groups)
/// that's microseconds and avoids N+1 queries per BFS level.
/// </para>
/// </summary>
public class PermissionService(IQuerySession session) : IPermissionService
{
    /// <summary>
    /// Wildcard slug on <see cref="Group.BoundTo"/> meaning "active in every
    /// app" — used by the realm-admin group.
    /// </summary>
    public const string AllAppsWildcard = "*";

    public async Task<bool> HasPermissionAsync(Guid userId, string appSlug, string permission, CancellationToken ct = default)
    {
        var permissions = await GetUserPermissionsAsync(userId, appSlug, ct);
        return PermissionEvaluator.Evaluate(permissions, permission);
    }

    public async Task<List<string>> GetUserPermissionsAsync(Guid userId, string appSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSlug);

        // Permissions flow Principal → Group → Role → Permission. No direct
        // user→role or user→permission grants — membership-via-group is the
        // sole path.
        var groups = await GetUserGroupsAsync(userId, ct);
        if (groups.Count == 0) return [];

        // BoundTo gates the group's contribution: "*" wildcard or the
        // requested app must be present. Empty BoundTo = dormant for
        // permission purposes (e.g. distribution-list groups).
        var activeGroups = groups
            .Where(g => g.BoundTo.Contains(AllAppsWildcard) || g.BoundTo.Contains(appSlug))
            .ToList();
        if (activeGroups.Count == 0) return [];

        var roleIds = activeGroups.SelectMany(g => g.RoleIds).Distinct().ToArray();
        if (roleIds.Length == 0) return [];

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds) && !r.IsDeleted)
            .ToListAsync(ct);

        // Resolve the requested App once. Roles whose AppId matches contribute
        // their PermissionIds; everything else is filtered out (apart from
        // pure-realm-admin roles which always contribute "realm:admin"
        // regardless of their AppId).
        var requestedApp = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == appSlug && !a.IsDeleted, ct);
        var requestedAppId = requestedApp?.Id;
        var requestedCatalog = requestedApp is null
            ? new Dictionary<Guid, AppPermission>()
            : requestedApp.Permissions.ToDictionary(p => p.Id);

        var permissions = new HashSet<string>();
        foreach (var role in roles)
        {
            if (role.IsRealmAdmin)
            {
                permissions.Add(PermissionEvaluator.RealmAdminPermission);
            }

            // Catalog-FK grants only contribute when the role belongs to the
            // requested App. A role bound to App X never leaks permissions
            // into App Y, even when its parent group is bound to "*".
            if (role.AppId.HasValue && role.AppId == requestedAppId)
            {
                foreach (var permissionId in role.PermissionIds)
                {
                    if (requestedCatalog.TryGetValue(permissionId, out var catalogEntry))
                    {
                        permissions.Add(catalogEntry.ToPermissionString());
                    }
                    // else: stale FK pointing at a deleted/missing catalog
                    // entry — silently dropped. The admin UI's catalog editor
                    // is expected to flag this on edit.
                }
            }
        }
        return permissions.ToList();
    }

    public async Task<List<Group>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default)
    {
        var allGroups = (await session.Query<Group>()
            .Where(g => !g.IsDeleted)
            .ToListAsync(ct)).ToList();

        var parentMap = BuildParentMap(allGroups);

        var resolved = new Dictionary<Guid, Group>();
        var visited = new HashSet<Guid> { userId };
        var queue = new Queue<Guid>();
        queue.Enqueue(userId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!parentMap.TryGetValue(currentId, out var parents)) continue;

            foreach (var parent in parents)
            {
                if (visited.Add(parent.Id))
                {
                    resolved[parent.Id] = parent;
                    queue.Enqueue(parent.Id);
                }
            }
        }

        return resolved.Values.ToList();
    }

    public async Task<List<PermissionRole>> GetUserRolesAsync(Guid userId, string appSlug, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSlug);

        var groups = await GetUserGroupsAsync(userId, ct);
        var activeGroups = groups
            .Where(g => g.BoundTo.Contains(AllAppsWildcard) || g.BoundTo.Contains(appSlug))
            .ToList();
        var roleIds = activeGroups.SelectMany(g => g.RoleIds).Distinct().ToList();

        if (roleIds.Count == 0)
            return [];

        // Resolve target App once so we can filter roles by AppId.
        var requestedApp = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == appSlug && !a.IsDeleted, ct);
        var requestedAppId = requestedApp?.Id;

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds.ToArray()) && !r.IsDeleted)
            .ToListAsync(ct);

        // Roles applicable to the requested app: same AppId, OR
        // realm-admin roles (which travel everywhere).
        return roles
            .Where(r => r.IsRealmAdmin || (requestedAppId.HasValue && r.AppId == requestedAppId))
            .ToList();
    }

    // ── Federation v1 union overloads (decision D) ────────────────────────

    public async Task<List<Group>> GetUserGroupsAsync(
        Guid userId, IReadOnlyCollection<Guid> sessionGroupIds, CancellationToken ct = default)
    {
        var resolved = await ResolveGroupsWithProvenanceAsync(userId, sessionGroupIds, ct);
        return resolved.Select(r => r.Group).ToList();
    }

    public async Task<List<string>> GetUserPermissionsAsync(
        Guid userId, string appSlug, IReadOnlyCollection<Guid> sessionGroupIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSlug);

        var resolved = await ResolveGroupsWithProvenanceAsync(userId, sessionGroupIds, ct);
        if (resolved.Count == 0) return [];

        var activeGroups = resolved
            .Where(r => r.Group.BoundTo.Contains(AllAppsWildcard) || r.Group.BoundTo.Contains(appSlug))
            .ToList();
        if (activeGroups.Count == 0) return [];

        // Role IDs reachable through a DURABLE (source=local) active group — the
        // only roles allowed to confer realm:admin. A session-sourced group, even
        // one whose ancestor carries a realm-admin role, must never grant it:
        // realm:admin is hard local-only (decision G). The write-time config guard
        // (a realm:admin-conferring group cannot be ExternallyDrivable) is the
        // first line; this provenance check is the second. <app>:admin and below
        // MAY be externally driven, so the catalog-FK grants below stay
        // provenance-agnostic.
        var localRoleIds = activeGroups
            .Where(r => r.IsLocal)
            .SelectMany(r => r.Group.RoleIds)
            .ToHashSet();

        var roleIds = activeGroups.SelectMany(r => r.Group.RoleIds).Distinct().ToArray();
        if (roleIds.Length == 0) return [];

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds) && !r.IsDeleted)
            .ToListAsync(ct);

        var requestedApp = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == appSlug && !a.IsDeleted, ct);
        var requestedAppId = requestedApp?.Id;
        var requestedCatalog = requestedApp is null
            ? new Dictionary<Guid, AppPermission>()
            : requestedApp.Permissions.ToDictionary(p => p.Id);

        var permissions = new HashSet<string>();
        foreach (var role in roles)
        {
            // PROVENANCE-AWARE realm:admin strip (federation decision G): emit the
            // synthetic realm:admin marker only when the realm-admin role is held
            // through a durable group. localRoleIds == all roleIds when there are
            // no session groups, so the non-federation path is unchanged.
            if (role.IsRealmAdmin && localRoleIds.Contains(role.Id))
            {
                permissions.Add(PermissionEvaluator.RealmAdminPermission);
            }

            if (role.AppId.HasValue && role.AppId == requestedAppId)
            {
                foreach (var permissionId in role.PermissionIds)
                {
                    if (requestedCatalog.TryGetValue(permissionId, out var catalogEntry))
                    {
                        permissions.Add(catalogEntry.ToPermissionString());
                    }
                }
            }
        }
        return permissions.ToList();
    }

    public async Task<List<PermissionRole>> GetUserRolesAsync(
        Guid userId, string appSlug, IReadOnlyCollection<Guid> sessionGroupIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appSlug);

        var resolved = await ResolveGroupsWithProvenanceAsync(userId, sessionGroupIds, ct);
        var activeGroups = resolved
            .Where(r => r.Group.BoundTo.Contains(AllAppsWildcard) || r.Group.BoundTo.Contains(appSlug))
            .ToList();
        var roleIds = activeGroups.SelectMany(r => r.Group.RoleIds).Distinct().ToList();
        if (roleIds.Count == 0)
            return [];

        var localRoleIds = activeGroups
            .Where(r => r.IsLocal)
            .SelectMany(r => r.Group.RoleIds)
            .ToHashSet();

        var requestedApp = await session.Query<App>()
            .FirstOrDefaultAsync(a => a.Slug == appSlug && !a.IsDeleted, ct);
        var requestedAppId = requestedApp?.Id;

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds.ToArray()) && !r.IsDeleted)
            .ToListAsync(ct);

        // A realm-admin role travels everywhere — but only when reached durably
        // (provenance guard, mirroring GetUserPermissionsAsync). App-scoped roles
        // are provenance-agnostic (externally drivable below realm level).
        return roles
            .Where(r => r.IsRealmAdmin
                ? localRoleIds.Contains(r.Id)
                : (requestedAppId.HasValue && r.AppId == requestedAppId))
            .ToList();
    }

    /// <summary>
    /// Federation v1 (decision D) — resolves the user's effective group set with
    /// per-group provenance, unioning durable membership with the session-derived
    /// <paramref name="sessionGroupIds"/>.
    /// <list type="bullet">
    ///   <item><b>Durable (source=local):</b> the BFS ancestors of
    ///   <paramref name="userId"/> — identical to
    ///   <see cref="GetUserGroupsAsync(Guid, CancellationToken)"/>.</item>
    ///   <item><b>Session:</b> each supplied group id (the membership itself) PLUS
    ///   its BFS ancestors — a session child still confers its parents' roles.</item>
    /// </list>
    /// A group reachable both ways is tagged <see cref="ResolvedGroup.IsLocal"/> =
    /// <c>true</c> (durable wins), so a legitimately-held realm:admin is never
    /// stripped. With an empty <paramref name="sessionGroupIds"/> the result is
    /// exactly the durable set, all local — keeping the no-arg methods' behavior.
    /// </summary>
    private async Task<List<ResolvedGroup>> ResolveGroupsWithProvenanceAsync(
        Guid userId, IReadOnlyCollection<Guid> sessionGroupIds, CancellationToken ct)
    {
        var allGroups = (await session.Query<Group>()
            .Where(g => !g.IsDeleted)
            .ToListAsync(ct)).ToList();

        var byId = allGroups.ToDictionary(g => g.Id);
        var parentMap = BuildParentMap(allGroups);

        // Durable pass: the user is not itself a group, so the seed is not
        // collected — only its ancestors (mirrors GetUserGroupsAsync(userId)).
        var local = new Dictionary<Guid, Group>();
        WalkAncestors([userId], includeSeeds: false, parentMap, byId, local);

        // Session pass: each matched session group IS a membership, so collect the
        // seed groups themselves and then walk their ancestors.
        var sessionResolved = new Dictionary<Guid, Group>();
        if (sessionGroupIds.Count > 0)
            WalkAncestors(sessionGroupIds, includeSeeds: true, parentMap, byId, sessionResolved);

        var resolved = new List<ResolvedGroup>(local.Count + sessionResolved.Count);
        foreach (var g in local.Values)
            resolved.Add(new ResolvedGroup(g, IsLocal: true));
        foreach (var (id, g) in sessionResolved)
            if (!local.ContainsKey(id))
                resolved.Add(new ResolvedGroup(g, IsLocal: false));
        return resolved;
    }

    /// <summary>
    /// BFS up the member-of graph from <paramref name="seeds"/>, collecting each
    /// reached group into <paramref name="into"/>. When
    /// <paramref name="includeSeeds"/> is true the seed groups themselves are
    /// collected (the user is a direct member of them); otherwise only their
    /// ancestors are.
    /// </summary>
    private static void WalkAncestors(
        IEnumerable<Guid> seeds,
        bool includeSeeds,
        Dictionary<Guid, List<Group>> parentMap,
        Dictionary<Guid, Group> byId,
        Dictionary<Guid, Group> into)
    {
        var visited = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        foreach (var seed in seeds)
        {
            if (!visited.Add(seed)) continue;
            queue.Enqueue(seed);
            if (includeSeeds && byId.TryGetValue(seed, out var seedGroup))
                into[seed] = seedGroup;
        }

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!parentMap.TryGetValue(currentId, out var parents)) continue;

            foreach (var parent in parents)
            {
                if (visited.Add(parent.Id))
                {
                    into[parent.Id] = parent;
                    queue.Enqueue(parent.Id);
                }
            }
        }
    }

    private readonly record struct ResolvedGroup(Group Group, bool IsLocal);

    public async Task<HashSet<Guid>> GetDescendantGroupIdsAsync(Guid groupId, CancellationToken ct = default)
    {
        var allGroups = (await session.Query<Group>()
            .Where(g => !g.IsDeleted)
            .ToListAsync(ct)).ToList();

        var byId = allGroups.ToDictionary(g => g.Id);
        var descendants = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(groupId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!byId.TryGetValue(current, out var group)) continue;

            foreach (var memberId in group.MemberIds)
            {
                if (byId.ContainsKey(memberId) && descendants.Add(memberId))
                    queue.Enqueue(memberId);
            }
        }

        return descendants;
    }

    /// <summary>
    /// For each principal id, returns the list of groups that have it as a direct
    /// member. Enables reverse "who am I a member of?" lookup without per-id queries.
    /// </summary>
    private static Dictionary<Guid, List<Group>> BuildParentMap(List<Group> allGroups)
    {
        var parentMap = new Dictionary<Guid, List<Group>>();
        foreach (var group in allGroups)
        {
            foreach (var memberId in group.MemberIds)
            {
                if (!parentMap.TryGetValue(memberId, out var parents))
                {
                    parents = [];
                    parentMap[memberId] = parents;
                }
                parents.Add(group);
            }
        }
        return parentMap;
    }
}
