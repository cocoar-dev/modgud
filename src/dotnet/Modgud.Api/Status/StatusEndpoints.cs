using System.Diagnostics;
using Cocoar.Reflectensions;
using Modgud.Authorization.AspNetCore;

namespace Modgud.Api;

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
                // HTTP-B9-03 — the connection's address, as the forwarded-headers policy
                // resolved it from trusted proxies; X-Forwarded-For itself is client input.
                ClientIp = context.Connection.RemoteIpAddress?.ToString(),
                Version = typeof(Program).Assembly.GetName().Version!.ToString(),
                UserAgent = request.Headers["User-Agent"].ToString(),

                // Shown as sent, unverified: whatever the client or a proxy put there.
                ProxyServers = request.Headers["X-Forwarded-For"]
                    .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToArray(),
                User = isAuthenticated ? context.User.Identity!.Name! : "Anonymous",
                Client = context.User.Claims.GetFirstClaimValueByType("client_id"),
                Authenticated = isAuthenticated,
                HostName = Environment.MachineName,
                ServiceStart = serviceStart,
                ServiceRunningSince = DateTime.Now - serviceStart,
                ContentRoot = environment.ContentRootPath,
                WebRoot = environment.WebRootPath
            };
        })
        // B9-J04 — host name, content paths, uptime, version and the proxy chain are
        // diagnostics for a realm admin, not for every signed-in user.
        .RequiresPermission("realm:admin");

        // Anonymous health check (for Docker HEALTHCHECK / Testcontainers)
        application.MapGet("api/health", () => Results.Ok(new { Status = "Healthy" }))
            .AllowAnonymous()
            .WithName("Health");

        return application;

    }
}