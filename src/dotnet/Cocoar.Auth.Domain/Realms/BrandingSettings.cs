namespace Cocoar.Auth.Domain.Realms;

/// <summary>
/// Per-realm branding overrides for the SPA — product name, logo, primary
/// color, favicon. All fields optional; missing = SPA falls back to the
/// Cocoar default.
///
/// <para>Public-readable: surfaced via <c>/api/app-info</c> so the login
/// page can render branded BEFORE the user is authenticated. Anonymous
/// callers see the same payload an authenticated user would.</para>
///
/// <para>v1 scope deliberately small (4 fields, URL-only — no asset
/// uploads, no light/dark variants, no per-page overrides). The full
/// drag-and-drop page-builder integration lands as a separate project
/// once <c>@cocoar/vue-page-builder</c> ships its first stable; see
/// <c>website/dev-notes/future-features/per-app-login-customization.md</c>
/// for that direction.</para>
///
/// <para>Stored as a JSONB sub-document on the tenant-DB
/// <c>RealmSettings</c> record — adding fields here doesn't need a
/// schema migration.</para>
/// </summary>
public record BrandingSettings
{
    /// <summary>
    /// Product name shown in the header + <c>document.title</c>. Defaults
    /// to "Cocoar.Auth" when null.
    /// </summary>
    public string? ProductName { get; init; }

    /// <summary>
    /// Absolute URL or SPA-relative path to the logo image (SVG / PNG).
    /// Rendered next to <c>ProductName</c> in the header and on the login
    /// page. Null = SPA falls back to the Cocoar logo.
    /// </summary>
    public string? LogoUrl { get; init; }

    /// <summary>
    /// Absolute URL or SPA-relative path to the favicon. Null = SPA keeps
    /// the default <c>/td-logo.svg</c>.
    /// </summary>
    public string? FaviconUrl { get; init; }

    /// <summary>
    /// CSS color value (hex like <c>#5A6478</c>, or any CSS-color string)
    /// applied as <c>--coar-color-primary</c> override at SPA boot. Null =
    /// the design-system default stays in effect.
    /// </summary>
    public string? PrimaryColor { get; init; }
}
