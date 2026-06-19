using BuildingBlocks.Helper;
using Modgud.Authentication.Domain;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Sessions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

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

            var result = await svc.RevokeSessionAsync(userId.Value, id, ct);
            return result.IsError ? result.ToResult() : Results.NoContent();
        })
        .WithName("Auth_Sessions_Revoke");

        // DELETE /api/auth/sessions — revoke all my sessions (logout everywhere).
        // Audit remediation #1: RevokeAllSessionsAsync alone only deleted tracking
        // rows — invisible to the cookie middleware, so other devices stayed signed
        // in for up to 30 days and OAuth tokens survived. Route through the kill
        // switch: rotate the security stamp (kills every cookie at the next
        // validator pass) + revoke OAuth tokens + delete session rows. Then refresh
        // THIS request so the acting device stays signed in; all others die.
        group.MapDelete("", [Authorize] async (
            HttpContext context,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IUserAccessRevoker accessRevoker,
            ISessionService sessionService,
            CancellationToken ct) =>
        {
            var userId = context.GetUserId();
            if (userId is null) return Results.Unauthorized();

            await accessRevoker.RevokeAllAccessAsync(userId.Value, AccessRevocationReason.ForceSignOut, ct);
            var user = await userManager.FindByIdAsync(userId.Value.ToString());
            if (user is not null)
            {
                await signInManager.RefreshSignInAsync(user);
                // RevokeAllAccessAsync deleted EVERY session row, including the acting
                // device's. RefreshSignInAsync keeps this device signed in but doesn't
                // re-track it — so without this the user's own "active sessions" list
                // would read empty until their next fresh login. Re-record the acting
                // session so the live device reappears.
                await SessionTracker.RecordLoginAsync(sessionService, context, userId.Value, ct);
            }
            return Results.NoContent();
        })
        .WithName("Auth_Sessions_RevokeAll");

        return application;
    }
}
