namespace Modgud.Domain.Realms;

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
    /// The realm's canonical public host — the designated primary among
    /// <see cref="Domains"/>. It is used for ALL outbound user-facing links
    /// (magic-link, password-reset, email-verify, bootstrap-invite,
    /// login-provider callbacks / redirect URIs) AND as the WebAuthn RP ID
    /// (relying-party identifier) for passkeys in this realm.
    ///
    /// <para>Invariant: it MUST be one of <see cref="Domains"/>. Changing it
    /// invalidates every existing passkey registered for the realm, because a
    /// passkey is cryptographically bound to the RP ID it was created
    /// against.</para>
    /// </summary>
    public string PrimaryDomain { get; set; } = string.Empty;

    /// <summary>
    /// True for the single Control-Plane realm of the deployment — the
    /// architectural anchor for cross-realm administration. STORED, not
    /// computed: exactly one realm carries the flag, and it is transferable
    /// to any active realm via
    /// <c>IRealmProvisioningService.TransferControlPlaneAsync</c> (in-app,
    /// control-plane-gated) or <c>recover control-plane transfer</c>
    /// (operator break-glass CLI). The bootstrap realm (slug
    /// <see cref="RealmSlugRules.SystemSlug"/>) is stamped with the flag at
    /// first boot, but the slug is only the default anchor name — it no
    /// longer determines control-plane status, so the bootstrap realm can
    /// become an equal, deletable peer once the flag moves elsewhere.
    ///
    /// <para>The Control Plane hosts the <c>/api/admin/realms/*</c>
    /// endpoints (gated by <c>ControlPlaneGateMiddleware</c>) and carries
    /// the <c>control-plane:*</c> permission namespace; tenant realms
    /// don't. Authority within it is the ordinary <c>realm:admin</c>
    /// permission, so moving the flag hands cross-realm administration to
    /// the target realm's existing admins with no permission migration.</para>
    ///
    /// <para>The "exactly one holder" invariant is enforced defensively by
    /// <c>TransferControlPlaneAsync</c> (it clears every other holder), not
    /// by a DB constraint — direct doc writes outside that path can break it,
    /// and a transfer self-heals it.</para>
    /// </summary>
    public bool IsControlPlane { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
