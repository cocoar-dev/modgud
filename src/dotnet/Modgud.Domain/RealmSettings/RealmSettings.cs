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

    /// <summary>Realm-wide policy for the shared Modgud browser/SSO session.
    /// Null = <see cref="BrowserSessionPolicy.Defaults"/>.</summary>
    public BrowserSessionPolicy? BrowserSessions { get; set; }

    /// <summary>Realm fallback for native OAuth client/device sessions.
    /// Applications and concrete OAuth clients may override it. Null =
    /// <see cref="ClientSessionPolicy.Defaults"/>.</summary>
    public ClientSessionPolicy? ClientSessions { get; set; }

    /// <summary>Per-realm overrides for the per-IP auth rate-limit ceilings
    /// (native-otp, magic-link, password-reset, email-otp, email-verification,
    /// passkey-begin, bootstrap). Null = never configured; every policy uses its
    /// shipped <see cref="AuthRateLimitDefaults"/>. A null rule for an individual
    /// policy likewise falls back to that policy's default.</summary>
    public AuthRateLimitSettings? AuthRateLimits { get; set; }

    /// <summary>Per-realm SPA branding (product name, logo, primary color,
    /// favicon). Null = SPA falls back to the Cocoar default. Surfaced via
    /// the anonymous <c>/api/app-info</c> so the login page renders branded
    /// before the user authenticates.</summary>
    public BrandingSettings? Branding { get; set; }

    /// <summary>Realm defaults for transactional-email copy. Null fields fall
    /// back to Branding and the built-in DE/EN template copy.</summary>
    public EmailBrandingSettings? EmailBranding { get; set; }

    /// <summary>Per-realm policy for which identity fields (username, given
    /// name, family name) are required when a user account is created. Email is
    /// always required and is not represented here. Null = never configured;
    /// callers read it as <see cref="RegistrationFieldsSettings.Defaults"/>
    /// (all three Optional — today's lenient behaviour). Overridable per
    /// Application (ADR-0011) and surfaced via <c>/api/app-info</c> so native
    /// apps + the web register form know which inputs to render.</summary>
    public RegistrationFieldsSettings? RegistrationFields { get; set; }

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

    /// <summary>LEGACY (pre-ADR-0001): single page-builder schema per
    /// SPA-page-slug. Retained only so <see cref="MigratePagesToSlots"/> can
    /// convert existing data into <see cref="PageSlots"/> on load; cleared on
    /// the next save. New reads/writes use <see cref="PageSlots"/>.</summary>
    public Dictionary<string, string>? Pages { get; set; }

    /// <summary>Page-builder configuration keyed by SPA-page-slug
    /// (<c>login</c>, <c>logout</c>, <c>password-forgot</c>, …). Each entry is
    /// a library of named variants plus which one is active (ADR-0001). A
    /// missing slot, or a slot whose <see cref="RealmPageSlot.ActiveVariantId"/>
    /// is null, renders the SPA's built-in hardcoded view.</summary>
    public Dictionary<string, RealmPageSlot>? PageSlots { get; set; }

    /// <summary>Reusable, immutable-versioned PageBuilder subtrees owned by
    /// this realm. Page drafts only embed materialized, pinned instances;
    /// published runtime pages contain no composition metadata.</summary>
    public List<PageComposition>? PageCompositions { get; set; }

    /// <summary>Lazily migrate the legacy single-schema <see cref="Pages"/>
    /// dictionary into <see cref="PageSlots"/> (ADR-0001). Idempotent and
    /// side-effect-only-when-needed: does nothing once migrated. Each legacy
    /// <c>Pages[slug] = schema</c> becomes one active variant named "Custom".
    /// Returns <c>true</c> when it changed the document (caller should persist).</summary>
    public bool MigratePagesToSlots()
    {
        if (Pages is null || Pages.Count == 0)
        {
            // Nothing legacy to migrate; drop the empty dictionary so it
            // doesn't linger in storage.
            if (Pages is not null) { Pages = null; return true; }
            return false;
        }

        PageSlots ??= new Dictionary<string, RealmPageSlot>(StringComparer.Ordinal);
        foreach (var (slug, schema) in Pages)
        {
            if (string.IsNullOrWhiteSpace(schema)) continue;
            if (PageSlots.ContainsKey(slug)) continue; // never clobber new data
            var id = Guid.NewGuid().ToString("N");
            PageSlots[slug] = new RealmPageSlot
            {
                Variants = [new PageVariant { Id = id, Name = "Custom", Schema = schema, CreatedAt = CreatedAt }],
                ActiveVariantId = id,
            };
        }
        Pages = null;
        return true;
    }
}
