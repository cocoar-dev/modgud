using Cocoar.Auth.Api;

namespace Cocoar.Auth.Api.Features.Admin;

public static class AppSettingsEndpoints
{
    public static WebApplication MapAppSettingsEndpoints(this WebApplication application, string path)
    {
        // Public endpoint — login page needs auth enforcement level + magic link availability
        application.MapGet($"{path}/app-info",
            (AppSettings settings) =>
            Results.Ok(new
            {
                settings.AuthenticationMinimumLevel,
                settings.MagicLinkSelfService,
                settings.TwoFactorGracePeriodDays,
            }))
        .WithName("AppInfo")
        .AllowAnonymous();

        return application;
    }
}
