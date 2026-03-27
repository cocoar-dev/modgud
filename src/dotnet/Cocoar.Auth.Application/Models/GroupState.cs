namespace Cocoar.Auth.Application.Models;

/// <summary>
/// Inline projection state for group validation in commands.
/// </summary>
public class GroupState
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public bool IsArchived { get; set; }
	public List<Guid> MemberIds { get; set; } = [];
	public List<Guid> ChildGroupIds { get; set; } = [];
	public List<GroupRealmRoleGrantData> RealmRoleGrants { get; set; } = [];
	public List<GroupClientRoleGrantData> ClientRoleGrants { get; set; } = [];
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? ModifiedAt { get; set; }
}

public record GroupRealmRoleGrantData(Guid RoleId);
public record GroupClientRoleGrantData(Guid RoleId, Guid ClientId);
