using Modgud.Infrastructure.Installation;

namespace Modgud.Api.Middleware;

/// <summary>
/// Keeps a zero-realm deployment closed until the shell-authorized first
/// installation completes. Health and installation assets/API remain reachable;
/// every normal API returns 503 and browser navigation is sent to /install.
/// </summary>
public sealed class InstallationGateMiddleware(RequestDelegate next)
{
    private volatile bool _knownInitialized;

    private static readonly string[] AllowedPrefixes =
    [
        "/api/install",
        "/install",
        "/health",
        "/assets",
        "/favicon",
    ];

    public async Task InvokeAsync(
        HttpContext context,
        IInstallationChallengeService installation)
    {
        if (_knownInitialized)
        {
            await next(context);
            return;
        }

        var status = await installation.GetStatusAsync(context.RequestAborted);
        if (status.IsInitialized)
        {
            _knownInitialized = true;
            await next(context);
            return;
        }

        if (IsAllowed(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method)
            && !context.Request.Path.StartsWithSegments("/api")
            && context.Request.Headers.Accept.Any(v =>
                v?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true))
        {
            context.Response.Redirect("/install");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "not_initialized",
            message = "This Modgud deployment has not been initialized.",
        }, context.RequestAborted);
    }

    private static bool IsAllowed(PathString path) =>
        AllowedPrefixes.Any(prefix => path.StartsWithSegments(prefix));
}
