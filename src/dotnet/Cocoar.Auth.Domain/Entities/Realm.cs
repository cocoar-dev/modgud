namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Represents a realm (tenant) in the multi-tenant identity system.
/// Stored as a Marten document in the system tenant database.
/// </summary>
public class Realm
{
	public Guid Id { get; set; }

	/// <summary>
	/// URL-safe identifier. Immutable after creation.
	/// Used as tenant ID and DB name suffix.
	/// </summary>
	public string Slug { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;
	public string? Description { get; set; }

	/// <summary>
	/// Domains that route to this tenant (e.g. ["acme.localhost", "auth.acme.com"]).
	/// The middleware matches the Host header against these domains.
	/// </summary>
	public string[] Domains { get; set; } = [];

	/// <summary>
	/// Whether this tenant can manage other tenants.
	/// Enables the /api/admin/realms endpoints for this tenant's users.
	/// At least one tenant must have this flag set to true.
	/// </summary>
	public bool CanManageTenants { get; set; }

	public bool IsActive { get; set; } = true;
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? UpdatedAt { get; set; }
}
