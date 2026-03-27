using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using Cocoar.Auth.Domain.Entities;
using Marten;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Computes the effective roles for a user by combining:
/// 1. Direct role assignments on the user
/// 2. Realm + client role grants from all groups the user belongs to (including nested groups)
/// </summary>
public class EffectiveRolesService : IEffectiveRolesService
{
	private readonly IQuerySession _session;
	private readonly IRoleRepository _roleRepository;

	public EffectiveRolesService(IQuerySession session, IRoleRepository roleRepository)
	{
		_session = session;
		_roleRepository = roleRepository;
	}

	public async Task<IReadOnlyList<ApplicationRole>> GetEffectiveRolesAsync(Guid userId, CancellationToken ct = default)
	{
		var effectiveRoleIds = new HashSet<Guid>();

		// 1. Direct role assignments
		var user = await _session.LoadAsync<ApplicationUser>(userId, ct);
		if (user is null) return [];

		foreach (var roleId in user.Roles)
			effectiveRoleIds.Add(roleId);

		// 2. Find all groups the user belongs to (direct + transitive via nesting)
		var userGroupIds = await ResolveUserGroupsAsync(userId, ct);

		// 3. Collect role grants from all groups
		foreach (var groupId in userGroupIds)
		{
			var group = await _session.LoadAsync<GroupState>(groupId, ct);
			if (group is null || group.IsArchived) continue;

			foreach (var grant in group.RealmRoleGrants)
				effectiveRoleIds.Add(grant.RoleId);

			foreach (var grant in group.ClientRoleGrants)
				effectiveRoleIds.Add(grant.RoleId);
		}

		// 4. Load all unique roles
		var roles = new List<ApplicationRole>();
		foreach (var roleId in effectiveRoleIds)
		{
			var role = await _roleRepository.GetByIdAsync(roleId, ct);
			if (role is not null)
				roles.Add(role);
		}

		return roles;
	}

	/// <summary>
	/// Resolves all groups a user belongs to — directly and transitively through group nesting.
	/// Walks UP the tree: if user is in "Backend Team" and "Backend Team" is child of "Engineering",
	/// the user effectively belongs to both.
	/// </summary>
	private async Task<HashSet<Guid>> ResolveUserGroupsAsync(Guid userId, CancellationToken ct)
	{
		var result = new HashSet<Guid>();

		// Load all non-archived groups
		var allGroupsList = (await _session.Query<GroupState>()
			.Where(g => !g.IsArchived)
			.ToListAsync(ct)).ToList();

		// Find groups where the user is a direct member
		var directGroups = allGroupsList.Where(g => g.MemberIds.Contains(userId)).ToList();

		foreach (var group in directGroups)
			CollectAncestorGroups(group.Id, allGroupsList, result);

		return result;
	}

	/// <summary>
	/// Collects the group and all its ancestor groups (parents that contain this group as a child).
	/// </summary>
	private static void CollectAncestorGroups(Guid groupId, List<GroupState> allGroups, HashSet<Guid> result)
	{
		if (!result.Add(groupId)) return; // Already visited — prevent cycles

		// Find all groups that have this group as a child
		var parents = allGroups.Where(g => g.ChildGroupIds.Contains(groupId));
		foreach (var parent in parents)
			CollectAncestorGroups(parent.Id, allGroups, result);
	}
}
