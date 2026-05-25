namespace Modgud.Domain.Realms;

/// <summary>
/// Per-realm branding overrides for the SPA — product name, logo, primary
/// color, favicon. Images reference assets in the per-realm asset library
/// (see <c>Modgud.Domain.Assets.Asset</c>) by id; the SPA-public
/// <c>/api/app-info</c> resolves them to <c>/assets/{id}</c> URLs on read.
/// All fields optional; missing = SPA falls back to the Cocoar default.
///
/// <para>v1 scope deliberately small (3 referenceable fields, no asset-
/// CDN, no per-page overrides). The full drag-and-drop page-builder
/// integration lands as a separate project once
/// <c>@cocoar/vue-page-builder</c> ships its first stable; see
/// <c>dev-docs/future-features/per-app-login-customization.md</c>
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
    /// to "Modgud" when null.
    /// </summary>
    public string? ProductName { get; init; }

    /// <summary>
    /// Asset id (from the per-realm asset library) for the logo image.
    /// Null = SPA falls back to the Modgud logo. The asset must exist in
    /// this realm's library — cross-realm references aren't possible by
    /// construction (each tenant's assets live in its own DB).
    /// </summary>
    public Guid? LogoAssetId { get; init; }

    /// <summary>
    /// Asset id for the favicon. Null = SPA keeps the default
    /// <c>/idp-logo.svg</c>.
    /// </summary>
    public Guid? FaviconAssetId { get; init; }

    /// <summary>
    /// CSS color value (hex like <c>#5A6478</c>, or any CSS-color string)
    /// applied as <c>--coar-color-primary</c> override at SPA boot. Null =
    /// the design-system default stays in effect. Validated server-side
    /// against a strict color-token regex on write.
    /// </summary>
    public string? PrimaryColor { get; init; }
}
