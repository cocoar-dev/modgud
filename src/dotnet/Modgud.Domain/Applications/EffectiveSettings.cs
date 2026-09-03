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
    public AuthRateLimitSettings? AuthRateLimits { get; init; }
    public ClientSessionPolicy? ClientSessions { get; init; }
    public BrandingSettings? Branding { get; init; }
    public RegistrationFieldsSettings? RegistrationFields { get; init; }
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
    public ApplicationPageTheme? PageTheme { get; init; }
    public ApplicationEmailBranding? EmailBranding { get; init; }
    public ApplicationLoginExperience? LoginExperience { get; init; }

    /// <summary>No Application in context: effective settings == the realm
    /// settings, section-for-section. The zero-behaviour path.</summary>
    public static EffectiveSettings From(RealmSettingsDoc realm) => new()
    {
        SelfRegistration = realm.SelfRegistration,
        Dcr = realm.Dcr,
        Cimd = realm.Cimd,
        NativeGrants = realm.NativeGrants,
        AuthRateLimits = realm.AuthRateLimits,
        ClientSessions = realm.ClientSessions,
        Branding = realm.Branding,
        RegistrationFields = realm.RegistrationFields,
        Deletion = realm.Deletion,
        Audit = realm.Audit,
        Pages = ResolveRealmActivePages(realm),
        SelfRegPosture = null,
        Origin = null,
        PageTheme = null,
        EmailBranding = MergeEmailBranding(realm.EmailBranding, null),
        LoginExperience = null,
    };

    /// <summary>An Application is in context: merge its (sparse) overrides
    /// field-by-field over the realm defaults. A null override section / field
    /// inherits the realm value, so an Application with no overrides yields the
    /// realm sections unchanged plus the Application-default facets.</summary>
    public static EffectiveSettings Merge(RealmSettingsDoc realm, ApplicationSettings app) => new()
    {
        // Sections the App can override (field-by-field):
        NativeGrants = MergeNativeGrants(realm.NativeGrants, app.NativeGrants),
        AuthRateLimits = AuthRateLimitSettings.Merge(realm.AuthRateLimits, app.AuthRateLimits),
        ClientSessions = MergeClientSessions(realm.ClientSessions, app.ClientSessions),
        Branding = MergeBranding(realm.Branding, app.Branding),
        SelfRegistration = MergeSelfRegistration(realm.SelfRegistration, app.SelfRegistration),
        Dcr = MergeDcr(realm.Dcr, app.Dcr),
        Cimd = MergeCimd(realm.Cimd, app.Cimd),
        RegistrationFields = MergeRegistrationFields(realm.RegistrationFields, app.RegistrationFields),

        // Realm-owned operational / GDPR sections have no per-App override:
        Deletion = realm.Deletion,
        Audit = realm.Audit,

        // Effective active page schema per slot: App selection (variant /
        // built-in) overrides the Realm selection when the App does not inherit
        // that slot; otherwise the Realm's active selection stands (ADR-0001).
        Pages = ResolveEffectivePages(realm, app),

        // New per-App facets:
        SelfRegPosture = app.SelfRegistration?.Posture ?? Applications.SelfRegPosture.JitOnOtp,
        Origin = app.Origin,
        PageTheme = app.PageTheme,
        EmailBranding = MergeEmailBranding(realm.EmailBranding, app.EmailBranding),
        LoginExperience = app.LoginExperience,
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

    private static ApplicationEmailBranding? MergeEmailBranding(
        Modgud.Domain.RealmSettings.EmailBrandingSettings? realm,
        ApplicationEmailBranding? app)
    {
        if (realm is null && app is null) return null;
        return new ApplicationEmailBranding
        {
            ProductName = app?.ProductName ?? realm?.ProductName,
            SubjectPrefix = app?.SubjectPrefix ?? realm?.SubjectPrefix,
            Preheader = app?.Preheader ?? realm?.Preheader,
            FooterText = app?.FooterText ?? realm?.FooterText,
            FromName = app?.FromName ?? realm?.FromName,
            FromAddress = app?.FromAddress ?? realm?.FromAddress,
            ReplyTo = app?.ReplyTo ?? realm?.ReplyTo,
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

    private static ClientSessionPolicy? MergeClientSessions(
        ClientSessionPolicy? realm,
        ApplicationClientSessionOverrides? app)
    {
        if (app is null) return realm;
        var policy = realm ?? ClientSessionPolicy.Defaults;
        return policy with
        {
            IdleLifetime = app.IdleLifetime ?? policy.IdleLifetime,
            AbsoluteLifetime = app.AbsoluteLifetime ?? policy.AbsoluteLifetime,
        };
    }

    // App override absent → realm passthrough. Present → each field is the App
    // value when set, else the realm value. Captcha fields stay realm-level (the
    // App override type doesn't carry them).
    private static SelfRegistrationSettings? MergeSelfRegistration(
        SelfRegistrationSettings? realm,
        ApplicationSelfRegistration? app)
    {
        if (app is null) return realm;
        var s = realm ?? new SelfRegistrationSettings();
        return s with
        {
            Enabled = app.Enabled ?? s.Enabled,
            RequireEmailVerification = app.RequireEmailVerification ?? s.RequireEmailVerification,
            AllowedEmailDomains = app.AllowedEmailDomains ?? s.AllowedEmailDomains,
            RequireAdminApproval = app.RequireAdminApproval ?? s.RequireAdminApproval,
            DefaultGroupIds = app.DefaultGroupIds ?? s.DefaultGroupIds,
            TermsOfServiceUrl = app.TermsOfServiceUrl ?? s.TermsOfServiceUrl,
            PrivacyPolicyUrl = app.PrivacyPolicyUrl ?? s.PrivacyPolicyUrl,
            // Captcha (CaptchaEnabled/SiteKey/EncryptedCaptchaSecret) untouched.
        };
    }

    private static DcrSettings? MergeDcr(DcrSettings? realm, ApplicationDcrOverrides? app)
    {
        if (app is null) return realm;
        var d = realm ?? new DcrSettings();
        return d with
        {
            Enabled = app.Enabled ?? d.Enabled,
            AccessTokenLifetime = app.AccessTokenLifetime ?? d.AccessTokenLifetime,
            RefreshTokenLifetime = app.RefreshTokenLifetime ?? d.RefreshTokenLifetime,
            GcTtlDays = app.GcTtlDays ?? d.GcTtlDays,
            PerIpRateLimitPerHour = app.PerIpRateLimitPerHour ?? d.PerIpRateLimitPerHour,
            PerRealmRateLimitPerDay = app.PerRealmRateLimitPerDay ?? d.PerRealmRateLimitPerDay,
            ReservedNames = app.ReservedNames ?? d.ReservedNames,
        };
    }

    private static CimdSettings? MergeCimd(CimdSettings? realm, ApplicationCimdOverrides? app)
    {
        if (app is null) return realm;
        var c = realm ?? new CimdSettings();
        return c with
        {
            Enabled = app.Enabled ?? c.Enabled,
            AccessTokenLifetime = app.AccessTokenLifetime ?? c.AccessTokenLifetime,
            RefreshTokenLifetime = app.RefreshTokenLifetime ?? c.RefreshTokenLifetime,
        };
    }

    private static RegistrationFieldsSettings? MergeRegistrationFields(
        RegistrationFieldsSettings? realm,
        ApplicationRegistrationFieldsOverrides? app)
    {
        if (app is null) return realm;
        var r = realm ?? new RegistrationFieldsSettings();
        return r with
        {
            Username = app.Username ?? r.Username,
            Firstname = app.Firstname ?? r.Firstname,
            Lastname = app.Lastname ?? r.Lastname,
        };
    }

    /// <summary>The realm's active schema per slot: the schema of each slot's
    /// active variant. Slots with no active variant (built-in) are omitted, so
    /// the SPA falls back to its hardcoded view. Legacy <see cref="RealmSettings.Pages"/>
    /// entries (pre-ADR-0001, not yet migrated) are honoured for any slug the
    /// new <c>PageSlots</c> does not cover.</summary>
    private static Dictionary<string, string>? ResolveRealmActivePages(RealmSettingsDoc realm)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (realm.PageSlots is not null)
        {
            foreach (var (slug, slot) in realm.PageSlots)
            {
                var schema = ActiveSchema(slot.Variants, slot.ActiveVariantId);
                if (schema is not null) result[slug] = schema;
            }
        }
        if (realm.Pages is not null)
        {
            foreach (var (slug, schema) in realm.Pages)
            {
                if (string.IsNullOrWhiteSpace(schema)) continue;
                if (realm.PageSlots?.ContainsKey(slug) == true) continue;
                result.TryAdd(slug, schema);
            }
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Effective active schema per slot with an Application in context:
    /// start from the realm's active pages, then apply the App's non-inherited
    /// slot selections (a variant, or built-in which removes the slot).</summary>
    private static Dictionary<string, string>? ResolveEffectivePages(
        RealmSettingsDoc realm,
        ApplicationSettings app)
    {
        var result = ResolveRealmActivePages(realm) is { } r
            ? new Dictionary<string, string>(r, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        if (app.PageSlots is not null)
        {
            foreach (var (slug, slot) in app.PageSlots)
            {
                if (slot.InheritActive) continue; // realm selection stands
                // Applications select from the REALM variant library.
                var realmVariants = realm.PageSlots?.GetValueOrDefault(slug)?.Variants;
                var schema = ActiveSchema(realmVariants, slot.ActiveVariantId);
                if (schema is not null) result[slug] = schema;
                else result.Remove(slug); // explicit built-in override
            }
        }
        return result.Count == 0 ? null : result;
    }

    private static string? ActiveSchema(List<PageVariant>? variants, string? activeId)
    {
        if (activeId is null || variants is null) return null; // built-in
        var v = variants.FirstOrDefault(x => x.Id == activeId);
        if (v is null) return null;
        // PublishedSchema was introduced after the original variant model.
        // Falling back to Schema keeps already-active legacy documents live;
        // every subsequent edit snapshots/publishes them through the new gate.
        var live = v.PublishedSchema ?? v.Schema;
        return string.IsNullOrWhiteSpace(live) ? null : live;
    }
}
