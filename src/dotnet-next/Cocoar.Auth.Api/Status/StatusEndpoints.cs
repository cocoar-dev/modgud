using System.Diagnostics;
using Cocoar.Reflectensions;
using Cocoar.Auth.Authentication.ExtensionMethods;

namespace Cocoar.Auth.Api;

public static class StatusEndpoints
{
    public static WebApplication MapStatusEndpoints(this WebApplication application)
    {
        application.MapGet("api/status", (HttpContext context, HttpRequest request, IWebHostEnvironment environment) =>
        {
            var isAuthenticated = context.User.Identity?.IsAuthenticated ?? false;
            var serviceStart = Process.GetCurrentProcess().StartTime;
            return new Status
            {
                ServiceName = typeof(Program).Assembly.GetName().Name!,
                CurrentDateTime = DateTime.Now,
                ClientIp = request.FindSourceIp().FirstOrDefault()?.ToString(),
                Version = typeof(Program).Assembly.GetName().Version!.ToString(),
                UserAgent = request.Headers["User-Agent"].ToString(),

                ProxyServers = request.FindSourceIp().Skip(1).Select(ip => ip.ToString()).ToArray(),
                User = isAuthenticated ? context.User.Identity!.Name! : "Anonymous",
                Client = context.User.Claims.GetFirstClaimValueByType("client_id"),
                Authenticated = isAuthenticated,
                HostName = Environment.MachineName,
                ServiceStart = serviceStart,
                ServiceRunningSince = DateTime.Now - serviceStart,
                ContentRoot = environment.ContentRootPath,
                WebRoot = environment.WebRootPath
            };
        }).RequireAuthorization();

        // Anonymous health check (for Docker HEALTHCHECK / Testcontainers)
        application.MapGet("api/health", () => Results.Ok(new { Status = "Healthy" }))
            .AllowAnonymous()
            .WithName("Health");

        return application;

    }
}