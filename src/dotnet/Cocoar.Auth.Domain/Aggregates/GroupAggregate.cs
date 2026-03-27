using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Event-sourced aggregate for group data.
/// Groups organize users and carry role grants (realm or client-scoped).
/// </summary>
public class GroupAggregate
{
	public Guid Id { get; private set; }
	public string Name { get; private set; } = string.Empty;
	public string? Description { get; private set; }
	public bool IsArchived { get; private set; }
	public List<Guid> MemberIds { get; private set; } = [];
	public List<Guid> ChildGroupIds { get; private set; } = [];
	public List<GroupRoleGrant> RealmRoleGrants { get; private set; } = [];
	public List<GroupClientRoleGrant> ClientRoleGrants { get; private set; } = [];
	public DateTimeOffset CreatedAt { get; private set; }
	public DateTimeOffset? ModifiedAt { get; private set; }

	// ── Lifecycle ──

	public void Apply(GroupCreated @event)
	{
		Id = @event.GroupId;
		Name = @event.Name;
		Description = @event.Description;
		CreatedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupRenamed @event)
	{
		Name = @event.NewName;
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupDescriptionChanged @event)
	{
		Description = @event.NewDescription;
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupArchived @event)
	{
		IsArchived = true;
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	// ── Membership ──

	public void Apply(GroupMemberAdded @event)
	{
		if (!MemberIds.Contains(@event.UserId))
			MemberIds.Add(@event.UserId);
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupMemberRemoved @event)
	{
		MemberIds.Remove(@event.UserId);
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	// ── Nesting ──

	public void Apply(GroupChildAdded @event)
	{
		if (!ChildGroupIds.Contains(@event.ChildGroupId))
			ChildGroupIds.Add(@event.ChildGroupId);
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupChildRemoved @event)
	{
		ChildGroupIds.Remove(@event.ChildGroupId);
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	// ── Role Grants ──

	public void Apply(GroupRealmRoleGranted @event)
	{
		if (!RealmRoleGrants.Any(g => g.RoleId == @event.RoleId))
			RealmRoleGrants.Add(new GroupRoleGrant(@event.RoleId));
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupRealmRoleRevoked @event)
	{
		RealmRoleGrants.RemoveAll(g => g.RoleId == @event.RoleId);
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupClientRoleGranted @event)
	{
		if (!ClientRoleGrants.Any(g => g.RoleId == @event.RoleId && g.ClientId == @event.ClientId))
			ClientRoleGrants.Add(new GroupClientRoleGrant(@event.RoleId, @event.ClientId));
		ModifiedAt = DateTimeOffset.UtcNow;
	}

	public void Apply(GroupClientRoleRevoked @event)
	{
		ClientRoleGrants.RemoveAll(g => g.RoleId == @event.RoleId && g.ClientId == @event.ClientId);
		ModifiedAt = DateTimeOffset.UtcNow;
	}
}

public record GroupRoleGrant(Guid RoleId);
public record GroupClientRoleGrant(Guid RoleId, Guid ClientId);
