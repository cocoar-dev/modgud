namespace Cocoar.Auth.Application.ReadModels;

/// <summary>
/// Denormalized read model optimized for the admin user list grid.
/// Contains pre-resolved role names — no joins needed at query time.
/// Built by async projection from User + Role events.
/// </summary>
public class UserListReadModel
{
	public Guid Id { get; set; }
	public string UserName { get; set; } = string.Empty;
	public string? Email { get; set; }
	public string? FirstName { get; set; }
	public string? LastName { get; set; }
	public bool IsActive { get; set; } = true;
	public bool IsDeleted { get; set; }
	public bool TwoFactorEnabled { get; set; }
	public DateTimeOffset? LockoutEnd { get; set; }
	public List<UserListRoleData> Roles { get; set; } = [];
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? ModifiedAt { get; set; }
}

/// <summary>
/// Embedded role data in UserListReadModel.
/// Stores both ID and name so the list can display role names without a join.
/// </summary>
public record UserListRoleData(Guid Id, string Name);
