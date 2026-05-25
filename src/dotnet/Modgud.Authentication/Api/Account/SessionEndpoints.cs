using BuildingBlocks.Helper;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Sessions;
using Microsoft.AspNetCore.Authorization;

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
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await svc.GetSessionsAsync(userId.Value, currentSessionId: null, ct);
            return result.ToResult();
        })
        .WithName("Auth_Sessions_List");

        // DELETE /api/auth/sessions/{id} — revoke a single session of mine
        group.MapDelete("{id:guid}", [Authorize] async (
            Guid id,
            HttpContext context,
            ISessionService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await svc.RevokeSessionAsync(userId.Value, id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Auth_Sessions_Revoke");

        // DELETE /api/auth/sessions — revoke all my sessions (logout everywhere)
        group.MapDelete("", [Authorize] async (
            HttpContext context,
            ISessionService svc,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var result = await svc.RevokeAllSessionsAsync(userId.Value, exceptSessionId: null, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Auth_Sessions_RevokeAll");

        return application;
    }
}
