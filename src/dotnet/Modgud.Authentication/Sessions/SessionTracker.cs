using Microsoft.AspNetCore.Http;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// Convenience helper for sign-in handlers — captures IP + UA from the
/// current <see cref="HttpContext"/> and persists a session row. Failures
/// are swallowed (logged would be added once Serilog wiring is consistent
/// across slices) so a session-tracking blip never breaks login.
/// </summary>
public static class SessionTracker
{
    public static async Task RecordLoginAsync(
        ISessionService sessions,
        HttpContext httpContext,
        Guid userId,
        CancellationToken ct = default)
    {
        try
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var ua = httpContext.Request.Headers.UserAgent.ToString();
            await sessions.CreateSessionAsync(userId, ip, ua, ct);
        }
        catch
        {
            // Swallow — session tracking is best-effort.
        }
    }
}
