namespace Modgud.Application.DTOs.Applications;

/// <summary>
/// ADR-0011 — read + patch shape for a per-Application settings override
/// (<c>GET</c>/<c>PATCH /api/admin/app/{id}/settings</c>). Sparse by design: every
/// section and field is nullable. A null section on GET = "this App overrides
/// nothing in that section" (it inherits the realm); on PATCH a null section =
/// "no change", and a provided section REPLACES that App's override (within it, a
/// null field = inherit the realm value). The same DTO serves both directions.
/// </summary>
public record ApplicationSettingsDto
{
    public ApplicationOriginDto? Origin { get; init; }
    public ApplicationBrandingDto? Branding { get; init; }
    public ApplicationPageThemeDto? PageTheme { get; init; }
    public ApplicationEmailBrandingDto? EmailBranding { get; init; }
    public ApplicationLoginExperienceDto? LoginExperience { get; init; }
    public ApplicationSelfRegistrationDto? SelfRegistration { get; init; }
    public ApplicationNativeGrantsDto? NativeGrants { get; init; }
    /// <summary>ADR 0007 — sparse rate-limit overrides for this App (null = inherit).</summary>
    public Modgud.Application.DTOs.RealmSettings.UpdateAuthRateLimitsDto? AuthRateLimits { get; init; }
    public ApplicationClientSessionsDto? ClientSessions { get; init; }
    public ApplicationDcrDto? Dcr { get; init; }
    public ApplicationCimdDto? Cimd { get; init; }
    public ApplicationRegistrationFieldsDto? RegistrationFields { get; init; }
    public ApplicationChangeFeedDto? ChangeFeed { get; init; }
}

public record ApplicationOriginDto
{
    /// <summary>The App's own subdomain (e.g. <c>acmelist.cocoar.app</c>). Must be a
    /// child of the realm's primary domain. Null/empty = no own origin (the App is
    /// reached via the tenant URL). Setting it also writes the global host→App
    /// routing map; clearing it removes the route.</summary>
    public string? Subdomain { get; init; }
}

public record ApplicationBrandingDto
{
    public string? ProductName { get; init; }
    public string? PrimaryColor { get; init; }
    public string? LogoAssetId { get; init; }
    public string? LogoUrl { get; init; }      // read-only (derived)
    public string? FaviconAssetId { get; init; }
    public string? FaviconUrl { get; init; }    // read-only (derived)
}

/// <summary>
/// Safe Cocoar token overrides for Application-selected custom pages. These
/// values are never applied to Modgud chrome or built-in auth views.
/// </summary>
public record ApplicationPageThemeDto
{
    public string? AccentColor { get; init; }
    public string? ErrorColor { get; init; }
    public int? ButtonRadiusPx { get; init; }
    public int? InputRadiusPx { get; init; }
    public int? CardRadiusPx { get; init; }
    public string? BodyFontFamily { get; init; }
    public string? TitleFontFamily { get; init; }
}

public record ApplicationEmailBrandingDto
{
    public string? ProductName { get; init; }
    public string? SubjectPrefix { get; init; }
    public string? Preheader { get; init; }
    public string? FooterText { get; init; }
    public string? FromName { get; init; }
    /// <summary>Sender address override for this App. Null = inherit.</summary>
    public string? FromAddress { get; init; }
    public string? ReplyTo { get; init; }
}

public record ApplicationLoginExperienceDto
{
    public bool? InternalLoginEnabled { get; init; }
    public bool? MagicLinkEnabled { get; init; }
    /// <summary>Ordered ShortGuid allow-list. Null means every enabled external
    /// provider; an empty array intentionally disables every external provider.</summary>
    public string[]? LoginProviderIds { get; init; }
}

public record ApplicationSelfRegistrationDto
{
    /// <summary>One of <c>Off</c> / <c>JitOnOtp</c> / <c>ExplicitEndpoint</c>.</summary>
    public string? Posture { get; init; }
    public bool? Enabled { get; init; }
    public bool? RequireEmailVerification { get; init; }
    public string[]? AllowedEmailDomains { get; init; }
    public bool? RequireAdminApproval { get; init; }
    public string[]? DefaultGroupIds { get; init; }
    public string? TermsOfServiceUrl { get; init; }
    public string? PrivacyPolicyUrl { get; init; }
}

public record ApplicationNativeGrantsDto
{
    public bool? Enabled { get; init; }
    public int? AccessTokenLifetimeMinutes { get; init; }
    public int? RefreshTokenLifetimeDays { get; init; }
}

public record ApplicationClientSessionsDto
{
    public int? IdleLifetimeDays { get; init; }
    public int? AbsoluteLifetimeDays { get; init; }
}

public record ApplicationDcrDto
{
    public bool? Enabled { get; init; }
    public int? AccessTokenLifetimeMinutes { get; init; }
    public int? RefreshTokenLifetimeDays { get; init; }
    public int? GcTtlDays { get; init; }
    public int? PerIpRateLimitPerHour { get; init; }
    public int? PerRealmRateLimitPerDay { get; init; }
    public string[]? ReservedNames { get; init; }
}

public record ApplicationCimdDto
{
    public bool? Enabled { get; init; }
    public int? AccessTokenLifetimeMinutes { get; init; }
    public int? RefreshTokenLifetimeDays { get; init; }
}

public record ApplicationRegistrationFieldsDto
{
    /// <summary>One of <c>Off</c> / <c>Optional</c> / <c>Required</c>. Null = inherit.</summary>
    public string? Username { get; init; }
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
}

/// <summary>
/// Explicit per-App opt-in and retention policy for the resumable consumer
/// change feed. Retention keeps both the complete age window and at least the
/// newest event count.
/// </summary>
public record ApplicationChangeFeedDto
{
    public bool Enabled { get; init; }
    public int MinimumRetentionAgeDays { get; init; } = 7;
    public int MinimumEventCount { get; init; } = 1_000;
}
