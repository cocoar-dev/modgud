using BuildingBlocks.Helper;
using Microsoft.AspNetCore.WebUtilities;
using Modgud.Api;
using Modgud.Authentication.Applications;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Modgud.Infrastructure.Realms;

namespace Modgud.Api.Features.Admin;

public static class AppSettingsEndpoints
{
    public static WebApplication MapAppSettingsEndpoints(this WebApplication application, string path)
    {
        // Public endpoint — login page + SPA bootstrap need this anonymously.
        // Includes IsControlPlane (C14) so the SPA can hide control-plane-only
        // navigation entries (Realms admin, setup wizard) on tenant hosts even
        // before the user is authenticated. The flag is sourced from the
        // resolved tenant — a tenant realm sees `false`, the Control-Plane
        // realm sees `true`. RealmMiddleware runs before the endpoint, so
        // TenantInfo is always populated when the request reaches us.
        //
        // Branding section is included anonymously because the login page
        // needs to render branded BEFORE the user authenticates. Branding
        // is metadata, no secrets — same disclosure surface as the existing
        // public realm settings.
        application.MapGet($"{path}/app-info",
            async (HttpContext http, AppSettings settings, IApplicationSettingsResolver settingsResolver, string? returnUrl) =>
            {
                var tenant = http.Items[TenantConstants.HttpContextTenantInfoKey] as TenantInfo;
                // ADR-0011 — Host-time: on an Application subdomain the branding is
                // the App's overrides merged over the realm branding, so the login
                // page renders App-branded; on a plain tenant host this resolves to
                // the realm branding unchanged.
                // On an App subdomain the Host already pins the Application. On the
                // canonical Realm host, the login challenge keeps the original
                // /connect/authorize URL in ?redirect=. Accept that local continuation
                // as a second signal so App branding/pages also work without a custom
                // subdomain. ResolveForRequestAsync still gives the Host pin precedence.
                var clientId = ExtractAuthorizeClientId(returnUrl);
                var effective = await settingsResolver.ResolveForRequestAsync(http, clientId, http.RequestAborted);
                var branding = effective.Branding;
                // ADR-0011 — publish the resolved (App⊕realm) registration-field
                // policy so native apps + the web register form render exactly the
                // inputs this App requires. Email is the always-required anchor and
                // is reported as such; the three configurable fields carry their
                // Off/Optional/Required requirement. Default (never configured) = all
                // three Optional (today's lenient behaviour).
                var registrationFields = effective.RegistrationFields ?? RegistrationFieldsSettings.Defaults;
                var loginExperience = effective.LoginExperience;
                return Results.Ok(new
                {
                    settings.AuthenticationMinimumLevel,
                    InternalLoginEnabled = loginExperience?.InternalLoginEnabled ?? true,
                    MagicLinkSelfService = settings.MagicLinkSelfService
                        && (loginExperience?.MagicLinkEnabled ?? true),
                    settings.TwoFactorGracePeriodDays,
                    IsControlPlane = tenant?.IsControlPlane ?? false,
                    Branding = new
                    {
                        ProductName = branding?.ProductName,
                        // Resolve the asset id to a public URL so the SPA
                        // can drop it straight into <img src>. No need to
                        // expose the raw asset id to anonymous callers.
                        LogoUrl = branding?.LogoAssetId is { } l
                            ? $"/api/assets/{ShortGuid.Encode(l)}"
                            : null,
                        FaviconUrl = branding?.FaviconAssetId is { } f
                            ? $"/api/assets/{ShortGuid.Encode(f)}"
                            : null,
                        PrimaryColor = branding?.PrimaryColor,
                    },
                    // Application-only design tokens. The SPA scopes these to
                    // its mounted custom-page wrapper; built-in pages never
                    // inherit them.
                    PageTheme = effective.PageTheme is null ? null : new
                    {
                        effective.PageTheme.AccentColor,
                        effective.PageTheme.ErrorColor,
                        effective.PageTheme.ButtonRadiusPx,
                        effective.PageTheme.InputRadiusPx,
                        effective.PageTheme.CardRadiusPx,
                        effective.PageTheme.BodyFontFamily,
                        effective.PageTheme.TitleFontFamily,
                    },
                    Features = new
                    {
                        settings.Features.PageBuilder,
                        settings.Features.FunctionTerminals,
                    },
                    Pages = settings.Features.PageBuilder
                        ? effective.Pages ?? new Dictionary<string, string>()
                        : new Dictionary<string, string>(),
                    RegistrationFields = new
                    {
                        Email = nameof(FieldRequirement.Required), // always required (the anchor)
                        Username = registrationFields.Username.ToString(),
                        Firstname = registrationFields.Firstname.ToString(),
                        Lastname = registrationFields.Lastname.ToString(),
                    },
                    Legal = new
                    {
                        TermsOfServiceUrl = SafeLegalUrl(effective.SelfRegistration?.TermsOfServiceUrl),
                        PrivacyPolicyUrl = SafeLegalUrl(effective.SelfRegistration?.PrivacyPolicyUrl),
                    },
                });
            })
        .WithName("AppInfo")
        .AllowAnonymous();

        return application;
    }

    /// <summary>
    /// Extracts a client id only from a bounded, same-origin authorization
    /// continuation. Arbitrary paths/URLs never influence anonymous branding.
    /// Full OAuth request validation still happens at /connect/authorize; this
    /// helper merely selects the effective App presentation before login.
    /// </summary>
    internal static string? ExtractAuthorizeClientId(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl.Length > 8192) return null;
        if (returnUrl.IndexOfAny(['\r', '\n', '\0', '\\']) >= 0) return null;

        var queryStart = returnUrl.IndexOf('?');
        var path = queryStart < 0 ? returnUrl : returnUrl[..queryStart];
        if (!string.Equals(path, "/connect/authorize", StringComparison.OrdinalIgnoreCase)) return null;
        if (queryStart < 0 || queryStart == returnUrl.Length - 1) return null;

        var query = QueryHelpers.ParseQuery(returnUrl[queryStart..]);
        if (!query.TryGetValue("client_id", out var clientIds) || clientIds.Count != 1) return null;
        var clientId = clientIds[0];
        return string.IsNullOrWhiteSpace(clientId) ? null : clientId;
    }

    private static string? SafeLegalUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.ToString()
            : null;
}
