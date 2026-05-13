using BuildingBlocks.Helper;
using Cocoar.Auth.Api;
using Cocoar.Auth.Authentication.RealmSettings;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Cocoar.Auth.Infrastructure.Realms;

namespace Cocoar.Auth.Api.Features.Admin;

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
            async (HttpContext http, AppSettings settings, IRealmSettingsService realmSettings) =>
            {
                var tenant = http.Items[TenantConstants.HttpContextTenantInfoKey] as TenantInfo;
                var realmDoc = await realmSettings.LoadAsync(http.RequestAborted);
                return Results.Ok(new
                {
                    settings.AuthenticationMinimumLevel,
                    settings.MagicLinkSelfService,
                    settings.TwoFactorGracePeriodDays,
                    IsControlPlane = tenant?.IsControlPlane ?? false,
                    Branding = new
                    {
                        ProductName = realmDoc.Branding?.ProductName,
                        // Resolve the asset id to a public URL so the SPA
                        // can drop it straight into <img src>. No need to
                        // expose the raw asset id to anonymous callers.
                        LogoUrl = realmDoc.Branding?.LogoAssetId is { } l
                            ? $"/api/assets/{ShortGuid.Encode(l)}"
                            : null,
                        FaviconUrl = realmDoc.Branding?.FaviconAssetId is { } f
                            ? $"/api/assets/{ShortGuid.Encode(f)}"
                            : null,
                        PrimaryColor = realmDoc.Branding?.PrimaryColor,
                    },
                });
            })
        .WithName("AppInfo")
        .AllowAnonymous();

        return application;
    }
}
