using Cocoar.Auth.Domain.Realms;

namespace Cocoar.Auth.Domain.RealmSettings;

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
}
