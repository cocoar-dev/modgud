using Cocoar.Auth.Application.DTOs.Realms;

namespace Cocoar.Auth.Application.DTOs.RealmSettings;

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
}

/// <summary>Patch payload for <c>PATCH /api/admin/realm-settings</c>.
/// Sections are <c>null</c>/missing = no change; non-null = merge
/// field-by-field per the section's own patch semantics
/// (see <see cref="UpdateSelfRegistrationDto"/>).</summary>
public record UpdateRealmSettingsDto
{
    public UpdateSelfRegistrationDto? SelfRegistration { get; init; }
}
