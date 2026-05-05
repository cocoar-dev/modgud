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
    /// architectural anchor for cross-realm administration. The Control
    /// Plane:
    /// <list type="bullet">
    ///   <item><description>Hosts the <c>/api/admin/realms/*</c> endpoints
    ///   (mounted only on its hostnames, see
    ///   <c>ControlPlaneGateMiddleware</c>).</description></item>
    ///   <item><description>Carries the <c>control-plane:realm:read|write</c>
    ///   permissions in its tenant DB; tenant realms don't.</description></item>
    ///   <item><description>Has its own users, OAuth clients, scopes —
    ///   functions as a regular realm for everything else.</description></item>
    /// </list>
    /// Exactly ONE realm per deployment carries this flag; the boot
    /// validation in <c>Program.cs</c> enforces that and that the
    /// configured <c>ControlPlane__Hostnames</c> match this realm's
    /// <see cref="Domains"/>.
    /// </summary>
    public bool IsControlPlane { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
