namespace Cocoar.Auth.Application.DTOs.RealmSettings;

/// <summary>Read shape for the Branding sub-section of
/// <c>/api/admin/realm-settings</c>. Also surfaced (without the admin
/// gate) via <c>/api/app-info</c> so the login page renders branded
/// before authentication. All fields nullable — null = SPA falls back
/// to the Cocoar default.</summary>
public record BrandingSettingsDto
{
    public string? ProductName { get; init; }
    public string? LogoUrl { get; init; }
    public string? FaviconUrl { get; init; }
    public string? PrimaryColor { get; init; }
}

/// <summary>Patch payload for the Branding sub-section. Each property is
/// nullable on the patch DTO with the same semantics as the section itself:
/// omitting the key means "no change", sending <c>null</c> means "clear"
/// (revert to Cocoar default), and any other value replaces.
///
/// <para>Because the underlying fields are already optional strings, the
/// patch DTO mirrors them 1:1 — clearing happens by explicitly sending
/// the field as <c>null</c> in JSON. Callers that want "no change" simply
/// omit the property.</para></summary>
public record UpdateBrandingSettingsDto
{
    public string? ProductName { get; init; }
    public string? LogoUrl { get; init; }
    public string? FaviconUrl { get; init; }
    public string? PrimaryColor { get; init; }
}
