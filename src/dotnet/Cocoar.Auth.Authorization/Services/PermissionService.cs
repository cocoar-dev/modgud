using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Roles;
using Marten;

namespace Cocoar.Auth.Authorization.Services;

/// <summary>
/// Default permission service. Resolves permissions via
/// Principal → Group → Role → Permission, with transitive group traversal.
/// <para>
/// BFS loads every non-deleted <see cref="Group"/> in one query and walks the
/// member-of graph in memory. For typical tenant sizes (hundreds of groups)
/// that's microseconds and avoids N+1 queries per BFS level.
/// </para>
/// </summary>
public class PermissionService(IQuerySession session) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken ct = default)
    {
        var permissions = await GetUserPermissionsAsync(userId, ct);
        return PermissionEvaluator.Evaluate(permissions, permission);
    }

    public async Task<List<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        // Permissions flow Principal → Group → Role → Permission. No direct
        // user→role or user→permission grants — membership-via-group is the
        // sole path.
        var groups = await GetUserGroupsAsync(userId, ct);
        if (groups.Count == 0) return [];

        var roleIds = groups.SelectMany(g => g.RoleIds).Distinct().ToArray();
        if (roleIds.Length == 0) return [];

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds) && !r.IsDeleted)
            .ToListAsync(ct);

        var permissions = new HashSet<string>();
        foreach (var role in roles)
        {
            foreach (var action in role.Permissions)
            {
                // Permissions may already be in "resource:action" form (legacy) or bare action;
                // the bare case gets prefixed with the role's resource type.
                permissions.Add(action.Contains(':') ? action : $"{role.ResourceType}:{action}");
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

    public async Task<List<PermissionRole>> GetUserRolesAsync(Guid userId, CancellationToken ct = default)
    {
        var groups = await GetUserGroupsAsync(userId, ct);
        var roleIds = groups.SelectMany(g => g.RoleIds).Distinct().ToList();

        if (roleIds.Count == 0)
            return [];

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds.ToArray()) && !r.IsDeleted)
            .ToListAsync(ct);

        return roles.ToList();
    }

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
