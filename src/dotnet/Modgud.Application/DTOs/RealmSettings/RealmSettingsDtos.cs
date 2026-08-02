using Modgud.Application.DTOs.Realms;

namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the tenant-scoped <c>RealmSettings</c>
/// aggregate. Surfaced via <c>GET /api/admin/realm-settings</c> — gated
/// by <c>realm-settings:read</c>; realm-admin gets it via the
/// <c>realm:admin</c> bypass. Sections are non-null on the wire so the
/// SPA never has to special-case "never configured" vs "configured to
/// defaults". The captcha-secret is never returned — only a
/// <c>CaptchaSecretSet</c> flag, same write-only pattern as the
/// login-provider client-secret.</summary>
public record RealmSettingsDto
{
    public SelfRegistrationDto SelfRegistration { get; init; } = new();
    public DcrSettingsDto Dcr { get; init; } = new();
    public CimdSettingsDto Cimd { get; init; } = new();
    public NativeGrantSettingsDto NativeGrants { get; init; } = new();
    public BrowserSessionPolicyDto BrowserSessions { get; init; } = new();
    public ClientSessionPolicyDto ClientSessions { get; init; } = new();
    public AuthRateLimitsDto AuthRateLimits { get; init; } = new();
    public BrandingSettingsDto Branding { get; init; } = new();
    public EmailBrandingSettingsDto EmailBranding { get; init; } = new();
    public RegistrationFieldsSettingsDto RegistrationFields { get; init; } = new();
    public DeletionSettingsDto Deletion { get; init; } = new();
    public AuditSettingsDto Audit { get; init; } = new();

    // PageBuilder page config (variants + active selection) is served by the
    // dedicated /api/admin/customization/pages endpoints (ADR-0001), not this
    // bulk settings DTO — the schemas are large and independently managed.
}

/// <summary>Patch payload for <c>PATCH /api/admin/realm-settings</c>.
/// Sections are <c>null</c>/missing = no change; non-null = merge
/// field-by-field per the section's own patch semantics
/// (see <see cref="UpdateSelfRegistrationDto"/>).</summary>
public record UpdateRealmSettingsDto
{
    public UpdateSelfRegistrationDto? SelfRegistration { get; init; }
    public UpdateDcrSettingsDto? Dcr { get; init; }
    public UpdateCimdSettingsDto? Cimd { get; init; }
    public UpdateNativeGrantSettingsDto? NativeGrants { get; init; }
    public UpdateBrowserSessionPolicyDto? BrowserSessions { get; init; }
    public UpdateClientSessionPolicyDto? ClientSessions { get; init; }
    public UpdateAuthRateLimitsDto? AuthRateLimits { get; init; }
    public UpdateBrandingSettingsDto? Branding { get; init; }
    public UpdateEmailBrandingSettingsDto? EmailBranding { get; init; }
    public UpdateRegistrationFieldsSettingsDto? RegistrationFields { get; init; }
    public UpdateDeletionSettingsDto? Deletion { get; init; }
    public UpdateAuditSettingsDto? Audit { get; init; }
}
