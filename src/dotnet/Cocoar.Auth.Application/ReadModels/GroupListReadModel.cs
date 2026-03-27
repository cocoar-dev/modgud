namespace Cocoar.Auth.Application.ReadModels;

/// <summary>
/// Denormalized read model for the admin group list grid.
/// </summary>
public class GroupListReadModel
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public bool IsArchived { get; set; }
	public int MemberCount { get; set; }
	public int ChildGroupCount { get; set; }
	public int RoleGrantCount { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? ModifiedAt { get; set; }
}
