namespace Cocoar.Auth.Application.ReadModels;

/// <summary>
/// Denormalized read model optimized for the admin role list grid.
/// Contains pre-computed user count — no joins needed at query time.
/// Built by async projection from Role + User events.
/// </summary>
public class RoleListReadModel
{
	public Guid Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public string? Description { get; set; }
	public string? DisplayName { get; set; }
	public bool IsDeleted { get; set; }
	public int UserCount { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? ModifiedAt { get; set; }
}
