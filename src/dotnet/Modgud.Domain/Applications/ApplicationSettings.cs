using Modgud.Domain.Realms;

namespace Modgud.Domain.Applications;

/// <summary>
/// Per-Application configuration overrides (ADR-0011). A tenant-scoped
/// document keyed by <see cref="Modgud.Authorization.Apps.App.Id"/> — one row
/// per Application that has any override. Sparse: every section is nullable,
/// and within a section every field is nullable, so "never configured" reads
/// as "inherit the realm default" with no separate exists-yet branch. The
/// effective config is computed by <see cref="EffectiveSettings.Merge"/>
/// (App overrides merged field-by-field over <see cref="RealmSettings"/>).
///
/// <para>Why a separate doc rather than extending the event-sourced
/// <see cref="Modgud.Authorization.Apps.App"/> aggregate (ADR-0011 D1): the
/// <c>App</c> aggregate is a permission catalog — folding config-mutation
/// events into it conflates two concerns. This doc instead mirrors the
/// existing <see cref="RealmSettings"/> singleton shape, so the cascade is a
/// trivial field-merge and the admin write path parallels
/// <c>RealmSettingsService</c>. Lazy-created on first write; absence = the
/// Application overrides nothing, so an existing realm with no Application
/// docs behaves exactly as today (zero migration).</para>
/// </summary>
public class ApplicationSettings
{
    /// <summary>The owning <see cref="Modgud.Authorization.Apps.App.Id"/>.
    /// One <c>ApplicationSettings</c> doc per App.</summary>
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The Application's own origin (subdomain). Null = no own
    /// origin; the Application is reached via the tenant URL and inherits its
    /// outer shell. Consumed from Phase 1 (Host → Application resolution).</summary>
    public ApplicationOrigin? Origin { get; set; }

    /// <summary>Per-Application SPA branding overrides, merged field-by-field
    /// over the realm <see cref="BrandingSettings"/>. Reuses the realm record
    /// because every branding field is already nullable. Null = inherit the
    /// realm branding wholesale.</summary>
    public BrandingSettings? Branding { get; set; }

    /// <summary>Per-Application email branding. Null = inherit the realm /
    /// deployment email branding. Consumed in Phase 6.</summary>
    public ApplicationEmailBranding? EmailBranding { get; set; }

    /// <summary>Per-Application self-registration facet: the
    /// <see cref="SelfRegPosture"/> plus per-field overrides of the realm
    /// <see cref="SelfRegistrationSettings"/> policy (captcha stays realm-level).
    /// Null section = inherit; a null <see cref="ApplicationSelfRegistration.Posture"/>
    /// within a present section resolves to the Application default
    /// (<see cref="Applications.SelfRegPosture.JitOnOtp"/>).</summary>
    public ApplicationSelfRegistration? SelfRegistration { get; set; }

    /// <summary>Per-Application native (cookieless) grant overrides, merged
    /// field-by-field over the realm <see cref="NativeGrantSettings"/>. Null =
    /// inherit the realm native-grant settings.</summary>
    public ApplicationNativeGrantOverrides? NativeGrants { get; set; }

    /// <summary>Per-Application Dynamic Client Registration overrides, merged
    /// field-by-field over the realm <see cref="DcrSettings"/>. Null = inherit.</summary>
    public ApplicationDcrOverrides? Dcr { get; set; }

    /// <summary>Per-Application Client ID Metadata Document (CIMD) overrides,
    /// merged field-by-field over the realm <see cref="CimdSettings"/>. Null =
    /// inherit.</summary>
    public ApplicationCimdOverrides? Cimd { get; set; }
}

/// <summary>An Application's own origin. Phase-1 resolution maps a host to an
/// App via the global <c>Realm.ApplicationDomains</c> map; this record holds
/// the canonical subdomain for the Application's outer shell.</summary>
public record ApplicationOrigin
{
    /// <summary>The Application's fully-qualified subdomain host (e.g.
    /// <c>amzettel.cocoar.app</c>). Null = no own origin.</summary>
    public string? Subdomain { get; init; }
}

/// <summary>Per-Application email branding overrides. The deployment-level
/// "from" address stays global (<c>EmailConfiguration</c>); only the
/// in-template product name / display varies per Application. All fields
/// nullable for field-by-field inheritance.</summary>
public record ApplicationEmailBranding
{
    /// <summary>Product name used in email subjects / bodies. Null = inherit
    /// the realm branding product name (or the "Modgud" default).</summary>
    public string? ProductName { get; init; }
}

/// <summary>Per-Application self-registration overrides: the posture plus
/// nullable mirrors of the realm <see cref="SelfRegistrationSettings"/> policy
/// fields. Captcha (site key / secret) is deliberately NOT overridable per-App —
/// it stays a realm/deployment concern. A null field inherits the realm value.</summary>
public record ApplicationSelfRegistration
{
    /// <summary>Registration posture. Null = inherit the Application default
    /// (<see cref="Applications.SelfRegPosture.JitOnOtp"/>).</summary>
    public SelfRegPosture? Posture { get; init; }

    public bool? Enabled { get; init; }
    public bool? RequireEmailVerification { get; init; }
    public string[]? AllowedEmailDomains { get; init; }
    public bool? RequireAdminApproval { get; init; }
    public string[]? DefaultGroupIds { get; init; }
    public string? TermsOfServiceUrl { get; init; }
    public string? PrivacyPolicyUrl { get; init; }
}

/// <summary>Nullable-field mirror of <see cref="NativeGrantSettings"/> so each
/// field can be individually overridden or inherited. A null field inherits
/// the realm value.</summary>
public record ApplicationNativeGrantOverrides
{
    public bool? Enabled { get; init; }
    public TimeSpan? AccessTokenLifetime { get; init; }
    public TimeSpan? RefreshTokenLifetime { get; init; }
}

/// <summary>Nullable-field mirror of <see cref="DcrSettings"/>. A null field
/// inherits the realm value. (GcTtlDays is read by the realm-iterating GC job,
/// not the per-request registration endpoint, so it stays effectively realm-level
/// even if set here.)</summary>
public record ApplicationDcrOverrides
{
    public bool? Enabled { get; init; }
    public TimeSpan? AccessTokenLifetime { get; init; }
    public TimeSpan? RefreshTokenLifetime { get; init; }
    public int? GcTtlDays { get; init; }
    public int? PerIpRateLimitPerHour { get; init; }
    public int? PerRealmRateLimitPerDay { get; init; }
    public string[]? ReservedNames { get; init; }
}

/// <summary>Nullable-field mirror of <see cref="CimdSettings"/>. A null field
/// inherits the realm value.</summary>
public record ApplicationCimdOverrides
{
    public bool? Enabled { get; init; }
    public TimeSpan? AccessTokenLifetime { get; init; }
    public TimeSpan? RefreshTokenLifetime { get; init; }
}
