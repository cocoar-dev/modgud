namespace Cocoar.Auth.Domain.Realms;

/// <summary>
/// Represents a realm (tenant) in the multi-tenant identity system.
/// Stored as a Marten document in the master (global) database — never inside
/// a tenant DB. The middleware resolves the request's tenant by matching the
/// Host header against <see cref="Domains"/>.
/// </summary>
public class Realm
{
    public Guid Id { get; set; }

    /// <summary>
    /// URL-safe identifier. Immutable after creation.
    /// Used as Marten tenant ID and DB name suffix (<c>{mainDb}_{slug}</c>).
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Domains that route to this tenant (e.g. ["acme.localhost", "auth.acme.com"]).
    /// The <c>RealmMiddleware</c> matches the Host header against these domains.
    /// </summary>
    public string[] Domains { get; set; } = [];

    /// <summary>
    /// Whether this tenant can manage other tenants.
    /// Enables the /api/admin/realms endpoints for this tenant's users.
    /// At least one realm must have this flag set to true.
    /// </summary>
    public bool CanManageTenants { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
