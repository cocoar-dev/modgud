using BuildingBlocks.Helper;
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
            async (HttpContext http, AppSettings settings, IApplicationSettingsResolver settingsResolver) =>
            {
                var tenant = http.Items[TenantConstants.HttpContextTenantInfoKey] as TenantInfo;
                // ADR-0011 — Host-time: on an Application subdomain the branding is
                // the App's overrides merged over the realm branding, so the login
                // page renders App-branded; on a plain tenant host this resolves to
                // the realm branding unchanged.
                var effective = await settingsResolver.ResolveForRequestAsync(http, clientId: null, http.RequestAborted);
                var branding = effective.Branding;
                // ADR-0011 — publish the resolved (App⊕realm) registration-field
                // policy so native apps + the web register form render exactly the
                // inputs this App requires. Email is the always-required anchor and
                // is reported as such; the three configurable fields carry their
                // Off/Optional/Required requirement. Default (never configured) = all
                // three Optional (today's lenient behaviour).
                var registrationFields = effective.RegistrationFields ?? RegistrationFieldsSettings.Defaults;
                return Results.Ok(new
                {
                    settings.AuthenticationMinimumLevel,
                    settings.MagicLinkSelfService,
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
                    Features = new
                    {
                        settings.Features.PageBuilder,
                    },
                    RegistrationFields = new
                    {
                        Email = nameof(FieldRequirement.Required), // always required (the anchor)
                        Username = registrationFields.Username.ToString(),
                        Firstname = registrationFields.Firstname.ToString(),
                        Lastname = registrationFields.Lastname.ToString(),
                    },
                });
            })
        .WithName("AppInfo")
        .AllowAnonymous();

        return application;
    }
}
