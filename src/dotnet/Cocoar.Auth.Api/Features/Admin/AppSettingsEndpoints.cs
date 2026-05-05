using Cocoar.Auth.Api;
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
        application.MapGet($"{path}/app-info",
            (HttpContext http, AppSettings settings) =>
            {
                var tenant = http.Items[TenantConstants.HttpContextTenantInfoKey] as TenantInfo;
                return Results.Ok(new
                {
                    settings.AuthenticationMinimumLevel,
                    settings.MagicLinkSelfService,
                    settings.TwoFactorGracePeriodDays,
                    IsControlPlane = tenant?.IsControlPlane ?? false,
                });
            })
        .WithName("AppInfo")
        .AllowAnonymous();

        return application;
    }
}
