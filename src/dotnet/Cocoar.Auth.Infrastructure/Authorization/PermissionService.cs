using Cocoar.Auth.Application.Authorization;
using Cocoar.Auth.Domain.Authorization;
using Marten;

namespace Cocoar.Auth.Infrastructure.Authorization;

public class PermissionService(IQuerySession session) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken ct = default)
    {
        var permissions = await GetUserPermissionsAsync(userId, ct);

        // Two super-admin scopes bypass everything:
        //   system:admin — only meaningful in the system realm; god-mode within that tenant.
        //   tenant:admin — per-realm bypass; implies all permissions in the current tenant.
        if (permissions.Contains(Permissions.SystemAdmin) || permissions.Contains(Permissions.TenantAdmin))
            return true;

        return permissions.Contains(permission);
    }

    public async Task<List<string>> GetUserPermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        // Permissions flow Principal → Group → Role → Permission. Direct user→role
        // and user→permission assignments do not exist in this system.
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
                // Roles store actions either bare ("read") or fully qualified ("user:read").
                // Normalize to fully qualified using the role's resource type.
                permissions.Add(action.Contains(':') ? action : $"{role.ResourceType}:{action}");
            }
        }
        return permissions.ToList();
    }

    public async Task<List<AuthorizationGroup>> GetUserGroupsAsync(Guid userId, CancellationToken ct = default)
    {
        // Load all non-deleted groups once; traverse the member-of graph in memory.
        // For typical tenant sizes (hundreds of groups) this is microseconds and
        // avoids N+1 queries per BFS level.
        var allGroups = (await session.Query<AuthorizationGroup>()
            .Where(g => !g.IsDeleted)
            .ToListAsync(ct)).ToList();

        var parentMap = BuildParentMap(allGroups);

        var resolved = new Dictionary<Guid, AuthorizationGroup>();
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
        var roleIds = groups.SelectMany(g => g.RoleIds).Distinct().ToArray();

        if (roleIds.Length == 0)
            return [];

        var roles = await session.Query<PermissionRole>()
            .Where(r => r.Id.IsOneOf(roleIds) && !r.IsDeleted)
            .ToListAsync(ct);

        return roles.ToList();
    }

    public async Task<HashSet<Guid>> GetDescendantGroupIdsAsync(Guid groupId, CancellationToken ct = default)
    {
        var allGroups = (await session.Query<AuthorizationGroup>()
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
                // Only follow if member is itself a group (present in the byId dict)
                if (byId.ContainsKey(memberId) && descendants.Add(memberId))
                {
                    queue.Enqueue(memberId);
                }
            }
        }

        return descendants;
    }

    /// <summary>
    /// For each principal id, returns the list of groups that have it as a direct member.
    /// Enables reverse lookup "who am I a member of?" without per-id queries.
    /// </summary>
    private static Dictionary<Guid, List<AuthorizationGroup>> BuildParentMap(List<AuthorizationGroup> allGroups)
    {
        var parentMap = new Dictionary<Guid, List<AuthorizationGroup>>();
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
