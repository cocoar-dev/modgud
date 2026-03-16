namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Represents a realm (tenant) in the multi-tenant identity system.
/// Stored in the master database's realms.realms table via raw SQL (not Marten).
/// </summary>
public class Realm
{
	public Guid Id { get; set; }

	/// <summary>
	/// URL-safe identifier. Immutable after creation.
	/// Used as tenant ID, DB name suffix, and URL segment.
	/// </summary>
	public string Slug { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;
	public string? Description { get; set; }
	public bool IsActive { get; set; } = true;
	public bool IsSystem { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? UpdatedAt { get; set; }
}
