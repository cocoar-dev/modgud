using BuildingBlocks.Helper;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.AspNetCore;

namespace Modgud.Authentication.Api.Admin;

/// <summary>
/// Admin operations for inspecting and force-logging-out other users'
/// sessions. Each endpoint is gated on the granular <c>session:*</c>
/// permissions so a "Help-Desk" role can read sessions without being
/// allowed to terminate them.
/// </summary>
public static class AdminSessionEndpoints
{
    public static WebApplication MapAdminSessionEndpoints(this WebApplication application, string path)
    {
        var group = application.MapGroup($"{path}/admin/users")
            .WithTags("Admin Sessions")
            .RequireAuthorization();

        // GET /api/admin/users/{id}/sessions — list a target user's sessions
        group.MapGet("{id}/sessions", async (
            string id,
            ISessionService svc,
            IClientSessionService clientSessions,
            CancellationToken ct) =>
        {
            var userId = ShortGuid.Decode(id);
            var result = await svc.GetSessionsAsync(userId, currentSessionId: null, ct);
            if (!result.IsError)
            {
                var clients = await clientSessions.GetSessionsAsync(userId, ct);
                result = result.Value with { ClientSessions = clients.ToList() };
            }
            return result.ToResult();
        })
        .WithName("Admin_Sessions_List")
        .RequiresPermission("session:read");

        // DELETE /api/admin/users/{id}/sessions — force-logout a target user.
        // A real kill switch: revoke OAuth tokens + device sessions + rotate the
        // security stamp (so the cookie dies at the next validation pass). Consent
        // grants are kept (ForceSignOut) — the account stays active and the user
        // can log back in.
        group.MapDelete("{id}/sessions", async (
            string id,
            IUserAccessRevoker accessRevoker,
            CancellationToken ct) =>
        {
            var userId = ShortGuid.Decode(id);
            await accessRevoker.RevokeAllAccessAsync(userId, AccessRevocationReason.ForceSignOut, ct);
            return Results.NoContent();
        })
        .WithName("Admin_Sessions_RevokeAll")
        .RequiresPermission("session:write");

        return application;
    }
}
