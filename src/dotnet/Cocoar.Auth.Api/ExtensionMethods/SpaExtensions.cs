using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using Cocoar.Auth.Api.Helper;

namespace Cocoar.Auth.Api.ExtensionMethods;

/// <summary>
/// Serves the Vue SPA from wwwroot with MapFallbackToFile for client-side routing.
/// In development, wwwroot is empty / missing — the Vite dev server (port 4300) handles
/// the frontend and proxies /api, /signalr, /connect, /signin-oidc, /signout-callback-oidc
/// back to the backend.
/// </summary>
public static class SpaExtensions
{
    public static void UseSpaUI(this WebApplication app)
    {
        var wwwRootPath = PathHelper.GetFullPath("wwwroot");

        if (!Directory.Exists(wwwRootPath))
            return;

        var fileProvider = new PhysicalFileProvider(wwwRootPath);

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            OnPrepareResponse = ctx =>
            {
                if (ctx.Context.Request.Path.ToString() == "/index.html")
                {
                    var headers = ctx.Context.Response.GetTypedHeaders();
                    headers.CacheControl = new CacheControlHeaderValue
                    {
                        Public = true,
                        MaxAge = TimeSpan.FromDays(0)
                    };
                }
            }
        });

        app.MapFallbackToFile("index.html", new StaticFileOptions
        {
            FileProvider = fileProvider
        });
    }
}
