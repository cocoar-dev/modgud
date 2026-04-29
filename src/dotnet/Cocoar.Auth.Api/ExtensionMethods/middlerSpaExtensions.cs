using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using Cocoar.Auth.Api.Helper;

namespace Cocoar.Auth.Api.ExtensionMethods;

public static class SpaExtensions
{
    /// <summary>
    /// Serves the SPA from wwwroot with MapFallbackToFile for client-side routing.
    /// In development, wwwroot may not exist — the Vite dev server handles the frontend
    /// (proxy configured in vite.config.ts).
    /// </summary>
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
