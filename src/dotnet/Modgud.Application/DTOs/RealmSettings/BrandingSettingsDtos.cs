using Modgud.Domain.Common;

namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the Branding sub-section of
/// <c>/api/admin/realm-settings</c>. Also surfaced (without the admin
/// gate) via <c>/api/app-info</c> so the login page renders branded
/// before authentication. Logo / favicon as resolved URLs (the SPA
/// drops them into <c>&lt;img src&gt;</c> directly); asset ids round-trip
/// through <see cref="LogoAssetId"/> + <see cref="FaviconAssetId"/> for
/// the admin form. All fields nullable — null = SPA falls back to the
/// Cocoar default.</summary>
public record BrandingSettingsDto
{
    public string? ProductName { get; init; }
    public string? LogoAssetId { get; init; }
    public string? LogoUrl { get; init; }
    public string? FaviconAssetId { get; init; }
    public string? FaviconUrl { get; init; }
    public string? PrimaryColor { get; init; }
}

/// <summary>Patch payload for the Branding sub-section (v2 merge-patch:
/// absent = no change, explicit <c>null</c> — or a blank string — clears
/// back to the Cocoar default, other = replace). <see cref="PrimaryColor"/>
/// is validated against a strict CSS color-token regex on write.</summary>
public record UpdateBrandingSettingsDto
{
    public Optional<string?> ProductName { get; init; }
    public Optional<string?> LogoAssetId { get; init; }
    public Optional<string?> FaviconAssetId { get; init; }
    public Optional<string?> PrimaryColor { get; init; }
}
