using Modgud.Domain.Realms;
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Domain.Applications;

/// <summary>
/// The resolved configuration for a request: <see cref="RealmSettings"/> with
/// any Application overrides merged in (ADR-0011 D1b — sparse, field-by-field).
/// The realm-owned sections keep <see cref="RealmSettings"/>'s nullability
/// (null = "never configured" = caller reads defaults), so a load-site that
/// today reads <c>realmSettings.X</c> can swap to <c>effective.X</c> with
/// identical semantics. New per-Application facets
/// (<see cref="SelfRegPosture"/> / <see cref="Origin"/> /
/// <see cref="EmailBranding"/>) have no realm equivalent.
///
/// <para><b>Zero-behaviour gate:</b> <see cref="From"/> (no Application in
/// context) copies the realm sections through unchanged and leaves the new
/// facets at their no-Application values, so an existing realm resolves to its
/// realm settings exactly.</para>
/// </summary>
public sealed record EffectiveSettings
{
    // ── Realm-owned sections (App overrides merged in where supported) ──
    public SelfRegistrationSettings? SelfRegistration { get; init; }
    public DcrSettings? Dcr { get; init; }
    public CimdSettings? Cimd { get; init; }
    public NativeGrantSettings? NativeGrants { get; init; }
    public BrandingSettings? Branding { get; init; }
    public DeletionSettings? Deletion { get; init; }
    public AuditSettings? Audit { get; init; }
    public Dictionary<string, string>? Pages { get; init; }

    // ── New per-Application facets (no RealmSettings equivalent) ──

    /// <summary>The Application's registration posture. <c>null</c> = no
    /// Application in context → legacy realm-only registration behaviour. When
    /// an Application is in context, this resolves to its configured posture or
    /// the default <see cref="SelfRegPosture.JitOnOtp"/>.</summary>
    public SelfRegPosture? SelfRegPosture { get; init; }

    public ApplicationOrigin? Origin { get; init; }
    public ApplicationEmailBranding? EmailBranding { get; init; }

    /// <summary>No Application in context: effective settings == the realm
    /// settings, section-for-section. The zero-behaviour path.</summary>
    public static EffectiveSettings From(RealmSettingsDoc realm) => new()
    {
        SelfRegistration = realm.SelfRegistration,
        Dcr = realm.Dcr,
        Cimd = realm.Cimd,
        NativeGrants = realm.NativeGrants,
        Branding = realm.Branding,
        Deletion = realm.Deletion,
        Audit = realm.Audit,
        Pages = realm.Pages,
        SelfRegPosture = null,
        Origin = null,
        EmailBranding = null,
    };

    /// <summary>An Application is in context: merge its (sparse) overrides
    /// field-by-field over the realm defaults. A null override section / field
    /// inherits the realm value, so an Application with no overrides yields the
    /// realm sections unchanged plus the Application-default facets.</summary>
    public static EffectiveSettings Merge(RealmSettingsDoc realm, ApplicationSettings app) => new()
    {
        // Sections the App can override today (field-by-field):
        NativeGrants = MergeNativeGrants(realm.NativeGrants, app.NativeGrants),
        Branding = MergeBranding(realm.Branding, app.Branding),

        // Realm-owned sections with no per-App override yet — passthrough:
        SelfRegistration = realm.SelfRegistration,
        Dcr = realm.Dcr,
        Cimd = realm.Cimd,
        Deletion = realm.Deletion,
        Audit = realm.Audit,
        Pages = realm.Pages,

        // New per-App facets:
        SelfRegPosture = app.SelfRegistration?.Posture ?? Applications.SelfRegPosture.JitOnOtp,
        Origin = app.Origin,
        EmailBranding = app.EmailBranding,
    };

    // App override absent → realm passthrough (incl. null). Present → each
    // field is the App value when set, else the realm value.
    private static BrandingSettings? MergeBranding(BrandingSettings? realm, BrandingSettings? app)
    {
        if (app is null) return realm;
        var b = realm ?? new BrandingSettings();
        return b with
        {
            ProductName = app.ProductName ?? b.ProductName,
            LogoAssetId = app.LogoAssetId ?? b.LogoAssetId,
            FaviconAssetId = app.FaviconAssetId ?? b.FaviconAssetId,
            PrimaryColor = app.PrimaryColor ?? b.PrimaryColor,
        };
    }

    private static NativeGrantSettings? MergeNativeGrants(
        NativeGrantSettings? realm,
        ApplicationNativeGrantOverrides? app)
    {
        if (app is null) return realm;
        var n = realm ?? new NativeGrantSettings();
        return n with
        {
            Enabled = app.Enabled ?? n.Enabled,
            AccessTokenLifetime = app.AccessTokenLifetime ?? n.AccessTokenLifetime,
            RefreshTokenLifetime = app.RefreshTokenLifetime ?? n.RefreshTokenLifetime,
        };
    }
}
