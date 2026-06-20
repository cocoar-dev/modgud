using Modgud.Domain.Realms;

namespace Modgud.Domain.RealmSettings;

/// <summary>
/// Per-realm configuration owned by the realm-admin (not the Control-Plane
/// admin). Lives in the tenant DB as a singleton document (one row,
/// addressed by <see cref="SingletonId"/>). Sections are nullable —
/// "never configured" reads as defaults, no separate "exists yet" branch
/// for callers.
///
/// <para>Why a separate doc from <see cref="Realm"/>: structural realm
/// metadata (slug, domains, IsControlPlane, IsActive) is CP-managed and
/// lives in the master DB. Realm-admin-owned config (self-registration,
/// future: branded templates, password-policy overrides, …) lives
/// tenant-scoped so the same permission-gated <c>/api/admin/realm-settings</c>
/// endpoint serves both CP-admins (own realm = system) and tenant
/// realm-admins, without needing CP-only gating.</para>
/// </summary>
public class RealmSettings
{
    /// <summary>Singleton-per-tenant: every tenant DB has exactly one
    /// <c>RealmSettings</c> doc with this Id. Picked as a fixed sentinel
    /// so the service can <c>LoadAsync</c> without first querying the
    /// realm to discover its own Id.</summary>
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-00000000A55E");

    public Guid Id { get; set; } = SingletonId;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Self-registration sub-section. Null = feature has never
    /// been touched for this realm; API treats null as equivalent to
    /// <c>SelfRegistrationSettings { Enabled = false }</c>.</summary>
    public SelfRegistrationSettings? SelfRegistration { get; set; }

    /// <summary>Dynamic Client Registration sub-section. Null = feature
    /// has never been touched for this realm; API treats null as
    /// equivalent to <c>DcrSettings { Enabled = false }</c>. When
    /// <see cref="DcrSettings.Enabled"/> is <c>false</c>, the public
    /// <c>/connect/register</c> endpoint refuses every request and the
    /// discovery document omits <c>registration_endpoint</c>.</summary>
    public DcrSettings? Dcr { get; set; }

    /// <summary>Client ID Metadata Document sub-section (CIMD).
    /// Null = feature has never been touched for this realm; callers read
    /// null as equivalent to <c>CimdSettings { Enabled = false }</c>. When
    /// <see cref="CimdSettings.Enabled"/> is <c>false</c>, the server does
    /// not resolve CIMD <c>client_id</c> URLs and the discovery document
    /// omits <c>client_id_metadata_document_supported</c>.</summary>
    public CimdSettings? Cimd { get; set; }

    /// <summary>Native (cookieless) passwordless token-grant sub-section
    /// (ADR-0010: <c>urn:cocoar:otp</c>, <c>urn:cocoar:magic</c>). Null = never
    /// configured; callers read null as
    /// <c>NativeGrantSettings { Enabled = false }</c>. The master gate for
    /// whether <c>/connect/token</c> accepts the native grants for this realm at
    /// all — per-client opt-in via the <c>gt:urn:cocoar:*</c> permission is an
    /// additional, separate gate.</summary>
    public NativeGrantSettings? NativeGrants { get; set; }

    /// <summary>Per-realm SPA branding (product name, logo, primary color,
    /// favicon). Null = SPA falls back to the Cocoar default. Surfaced via
    /// the anonymous <c>/api/app-info</c> so the login page renders branded
    /// before the user authenticates.</summary>
    public BrandingSettings? Branding { get; set; }

    /// <summary>Per-realm account-deletion policy (self-service grace +
    /// admin recycle-bin retention). Null = never configured; callers read
    /// it as <see cref="DeletionSettings.Defaults"/>.</summary>
    public DeletionSettings? Deletion { get; set; }

    /// <summary>Per-realm tenant-audit visibility window (audit redesign §A.6).
    /// Null = never configured; callers read it as
    /// <see cref="AuditSettings.Defaults"/>. A *visibility* window over the
    /// rebuildable <c>AuthAuditView</c> — it bounds what the read surface shows, it
    /// does NOT delete history (the source events live with the aggregate, masked on
    /// erase).</summary>
    public AuditSettings? Audit { get; set; }

    /// <summary>Page-builder schemas keyed by SPA-page-slug
    /// (<c>login</c>, <c>logout</c>, <c>password-forgot</c>, …). Each
    /// value is the serialised <c>PageNode</c> tree as JSON. Missing key
    /// or empty value = render the SPA's hardcoded view for that page.
    /// Dictionary keeps the slug-set extensible without a schema change
    /// when more page-slots get adopted.</summary>
    public Dictionary<string, string>? Pages { get; set; }
}
