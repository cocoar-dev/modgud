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
    /// True for the single Control-Plane realm of the deployment — the
    /// architectural anchor for cross-realm administration. Computed from
    /// <see cref="Slug"/>: the realm with slug <see cref="RealmSlugRules.SystemSlug"/>
    /// (currently <c>"system"</c>) is the Control Plane, every other
    /// realm is data-plane. The slug is reserved (no tenant can claim it)
    /// and immutable — so this property is also immutable, and there's
    /// nothing to enforce or persist alongside it.
    ///
    /// <para>The Control Plane hosts the <c>/api/admin/realms/*</c>
    /// endpoints (gated by <c>ControlPlaneGateMiddleware</c>) and carries
    /// the <c>control-plane:*</c> permission namespace; tenant realms
    /// don't.</para>
    /// </summary>
    public bool IsControlPlane => Slug == RealmSlugRules.SystemSlug;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
