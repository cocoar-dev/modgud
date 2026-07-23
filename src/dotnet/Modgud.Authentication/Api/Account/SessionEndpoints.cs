using BuildingBlocks.Helper;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication;

namespace Modgud.Authentication.Api.Account;

/// <summary>
/// Per-user session management. Lets a signed-in user inspect and revoke
/// their own active devices/sessions. Admin endpoints for force-logout
/// of other users live in <c>AdminSessionEndpoints</c>.
/// </summary>
public static class SessionEndpoints
{
    public static WebApplication MapSessionEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/auth/sessions")
            .WithTags("Sessions")
            .RequireAuthorization();

        // GET /api/auth/sessions — list my active sessions
        group.MapGet("", [Authorize] async (
            HttpContext context,
            ISessionService svc,
            IClientSessionService clientSessions,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var currentSessionId = Guid.TryParse(
                context.User.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value,
                out var parsedSessionId)
                ? parsedSessionId
                : (Guid?)null;
            var result = await svc.GetSessionsAsync(userId.Value, currentSessionId, ct);
            if (!result.IsError)
            {
                var clients = await clientSessions.GetSessionsAsync(userId.Value, ct);
                result = result.Value with { ClientSessions = clients.ToList() };
            }
            return result.ToResult();
        })
        .WithName("Auth_Sessions_List");

        // DELETE /api/auth/sessions/{id} — revoke a single session of mine.
        // LIMITATION: this deletes the tracking row only. The auth cookie carries
        // no session-id binding, so a *targeted* single-device revoke cannot
        // invalidate that device's cookie (it keeps authenticating until expiry).
        // Truly killing one specific device needs a session-id claim on the cookie
        // + a per-request existence check in OnValidatePrincipal — tracked as a
        // follow-up. "Log out everywhere" (below) IS effective via stamp rotation.
        group.MapDelete("{id:guid}", [Authorize] async (
            Guid id,
            HttpContext context,
            ISessionService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var current = context.User.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value;
            if (Guid.TryParse(current, out var currentSessionId) && currentSessionId == id)
                return Results.Conflict(new { Error = "Use normal logout to end the current browser session." });

            var result = await svc.RevokeSessionAsync(userId.Value, id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Auth_Sessions_Revoke");

        group.MapDelete("client/{id:guid}", [Authorize] async (
            Guid id,
            HttpContext context,
            IClientSessionService clientSessions,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();
            var result = await clientSessions.RevokeAsync(userId.Value, id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Auth_ClientSessions_Revoke");

        group.MapDelete("others", [Authorize] async (
            HttpContext context,
            ISessionService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            var raw = context.User.FindFirst(SessionClaimTypes.BrowserSessionId)?.Value;
            if (userId is null || !Guid.TryParse(raw, out var currentSessionId))
                return Results.Unauthorized();
            var result = await svc.RevokeAllSessionsAsync(userId.Value, currentSessionId, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Auth_Sessions_RevokeOthers");

        // DELETE /api/auth/sessions — revoke all my sessions (logout everywhere).
        // Audit remediation #1: RevokeAllSessionsAsync alone only deleted tracking
        // rows — invisible to the cookie middleware, so other devices stayed signed
        // in for up to 30 days and OAuth tokens survived. Route through the kill
        // switch: rotate the security stamp (kills every cookie at the next
        // validator pass) + revoke OAuth tokens + delete session rows. Then refresh
        // THIS request so the acting device stays signed in; all others die.
        group.MapDelete("", [Authorize] async (
            HttpContext context,
            IUserAccessRevoker accessRevoker,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            await accessRevoker.RevokeAllAccessAsync(userId.Value, AccessRevocationReason.ForceSignOut, ct);
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        })
        .WithName("Auth_Sessions_RevokeAll");

        return application;
    }
}
