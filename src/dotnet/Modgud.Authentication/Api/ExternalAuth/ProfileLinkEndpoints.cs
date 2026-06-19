using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Modgud.Authorization.AspNetCore;
using Modgud.Authentication.ExtensionMethods;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Domain.LoginProviders;

namespace Modgud.Authentication.Api.ExternalAuth;

/// <summary>
/// Self-service endpoints a user hits from Profile → Security → Linked
/// accounts. List the IdP identities bound to the current user; disconnect
/// one. Linking itself is triggered via the OIDC flow
/// (<c>/account/external-login/{id}/start</c>) — once the user is already
/// signed in the finish endpoint routes the external ticket into
/// <c>ExternalLoginProcessor.ProcessAsync(authenticatedUserId: ...)</c>
/// which creates the link.
/// <para>
/// Type-discriminator posture: only <see cref="LoginProviderType.Oidc"/>
/// providers can be linked. The gate lives in two places — <c>/start</c>
/// rejects non-Oidc ids before the OIDC challenge is issued, and
/// <c>ExternalLoginProcessor</c> rejects again on the callback. The list
/// endpoints below intentionally surface every link the user has, including
/// any that may have come from a provider whose type was later changed —
/// disconnecting a stale link must remain possible.
/// </para>
/// </summary>
public static class ProfileLinkEndpoints
{
    public static void MapProfileLinkEndpoints(this IEndpointRouteBuilder endpoints, string path)
    {
        var group = endpoints.MapGroup($"{path}/account/external-links")
            .RequireAuthorization();

        group.MapGet("", async (
            HttpContext http,
            [FromServices] IQuerySession session,
            CancellationToken ct) =>
        {
            var userId = http.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var links = await session.Query<ExternalIdentityLink>()
                .Where(l => l.UserId == userId.Value && !l.IsUnlinked)
                .OrderByDescending(l => l.LastLoginAt)
                .ToListAsync(ct);

            var providerIds = links.Select(l => l.LoginProviderId).Distinct().ToArray();
            var providers = await session.Query<LoginProvider>()
                .Where(c => providerIds.Contains(c.Id))
                .ToListAsync(ct);
            var providerByName = providers.ToDictionary(c => c.Id, c => c.DisplayName);

            return Results.Ok(links.Select(l => ToDto(l, providerByName)).ToArray());
        });

        // Admin-side: list the external-identity links of any user, including
        // the last known claim snapshot per provider. Used by the User admin
        // page so ops can see "what did Entra send last for this user".
        endpoints.MapGet($"{path}/admin/users/{{userId}}/external-links", async (
            ShortGuid userId,
            [FromServices] IQuerySession session,
            CancellationToken ct) =>
        {
            var uid = userId.Guid;
            var links = await session.Query<ExternalIdentityLink>()
                .Where(l => l.UserId == uid && !l.IsUnlinked)
                .OrderByDescending(l => l.LastLoginAt)
                .ToListAsync(ct);

            var providerIds = links.Select(l => l.LoginProviderId).Distinct().ToArray();
            var providers = await session.Query<LoginProvider>()
                .Where(c => providerIds.Contains(c.Id))
                .ToListAsync(ct);
            var providerByName = providers.ToDictionary(c => c.Id, c => c.DisplayName);

            return Results.Ok(links.Select(l => ToDto(l, providerByName)).ToArray());
        })
        .RequireAuthorization()
        .RequiresPermission("user:read");

        // Self-service disconnect. Variant C — "unlink forgets the binding":
        // hard-deletes + archives the link so the same external identity can be
        // re-linked on a later login (to this or, once released, another user).
        group.MapDelete("{linkId}", async (
            ShortGuid linkId,
            HttpContext http,
            TimeProvider clock,
            [FromServices] IDocumentSession writeSession,
            [FromServices] UserManager<ApplicationUser> userManager,
            [FromServices] ILoggerFactory loggerFactory,
            [FromServices] Modgud.Authentication.Sessions.IUserAccessRevoker accessRevoker,
            CancellationToken ct) =>
        {
            var userId = http.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var link = await writeSession.LoadAsync<ExternalIdentityLink>(linkId.Guid, ct);
            if (link is null || link.IsUnlinked) return Results.NotFound();
            if (link.UserId != userId.Value) return Results.Forbid();

            return await UnlinkAsync(writeSession, userManager, accessRevoker,
                loggerFactory.CreateLogger(UnlinkLogCategory), clock, link, isAdmin: false, ct);
        });

        // Admin force-unlink: disconnect any user's external link. Same
        // hard-delete + last-auth-method guard as self-service; gated on
        // user:write. (An admin who wants the account gone deletes the user —
        // force-unlink must not silently strip a user's last remaining factor.)
        endpoints.MapDelete($"{path}/admin/users/{{userId}}/external-links/{{linkId}}", async (
            ShortGuid userId,
            ShortGuid linkId,
            TimeProvider clock,
            [FromServices] IDocumentSession writeSession,
            [FromServices] UserManager<ApplicationUser> userManager,
            [FromServices] ILoggerFactory loggerFactory,
            [FromServices] Modgud.Authentication.Sessions.IUserAccessRevoker accessRevoker,
            CancellationToken ct) =>
        {
            var link = await writeSession.LoadAsync<ExternalIdentityLink>(linkId.Guid, ct);
            if (link is null || link.IsUnlinked || link.UserId != userId.Guid) return Results.NotFound();

            return await UnlinkAsync(writeSession, userManager, accessRevoker,
                loggerFactory.CreateLogger(UnlinkLogCategory), clock, link, isAdmin: true, ct);
        })
        .RequireAuthorization()
        .RequiresPermission("user:write");
    }

    private const string UnlinkLogCategory = "Modgud.Authentication.ExternalAuth.Unlink";

    /// <summary>
    /// Shared unlink core for the self-service and admin force-unlink endpoints.
    /// Enforces the last-auth-method guard, then "forgets the binding" (Variant C):
    /// appends the terminal <see cref="ExternalIdentityUnlinkedEvent"/> on the link
    /// stream — the inline projection's <c>ShouldDelete</c> drops the projection doc,
    /// freeing the <c>(Issuer, Subject)</c> slot for a later re-link without leaving a
    /// blocking tombstone and without archiving (so a later GDPR erase can still mask
    /// the stream's PII) — plus the user-stream
    /// <see cref="UserExternalIdentityUnlinkedEvent"/> (which keeps
    /// <c>Person.ExternalIdentities</c> + <c>UserView</c> in sync and drives the
    /// auto-membership recompute).
    /// </summary>
    private static async Task<IResult> UnlinkAsync(
        IDocumentSession writeSession,
        UserManager<ApplicationUser> userManager,
        Modgud.Authentication.Sessions.IUserAccessRevoker accessRevoker,
        ILogger logger,
        TimeProvider clock,
        ExternalIdentityLink link,
        bool isAdmin,
        CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(link.UserId.ToString());

        // Self-lockout guard: refuse to remove the last remaining auth method
        // (no local password AND no passkey AND no other live external link).
        if (user is not null)
        {
            var otherLinks = await writeSession.Query<ExternalIdentityLink>()
                .Where(l => l.UserId == link.UserId && !l.IsUnlinked && l.Id != link.Id)
                .AnyAsync(ct);
            var hasPassword = await userManager.HasPasswordAsync(user);
            var hasPasskey = await writeSession.Query<StoredPasskeyCredential>()
                .AnyAsync(c => c.UserId == link.UserId, ct);
            if (!otherLinks && !hasPassword && !hasPasskey)
            {
                return Results.BadRequest(new
                {
                    Code = "Idp.LastAuthMethod",
                    Message = "Cannot remove the only remaining authentication method. Set a password or add another provider first.",
                });
            }
        }

        var now = clock.GetUtcNow();
        // Terminal event on the link stream → ShouldDelete drops the projection doc
        // (frees the unique slot, rebuild-safe, stream stays maskable).
        writeSession.Events.Append(link.Id,
            new ExternalIdentityUnlinkedEvent(link.Id, now, link.UserId));
        writeSession.Events.Append(link.UserId,
            new UserExternalIdentityUnlinkedEvent(link.UserId, link.Id, link.LoginProviderId, now));
        await writeSession.SaveChangesAsync(ct);

        // Audit remediation #5: the federated cookie is validated solely by the
        // security stamp, and the linkId claim is never re-checked against live link
        // state — so unlinking (esp. admin force-unlink of a compromised route) left
        // the in-flight session + tokens alive. Revoke live access on unlink. (Self-
        // service also revokes; keeping the actor's current session via RefreshSignIn
        // is a deferred UX refinement — re-auth after removing a login method is safe.)
        await accessRevoker.RevokeAllAccessAsync(link.UserId, Modgud.Authentication.Sessions.AccessRevocationReason.ForceSignOut, ct);

        logger.LogInformation(
            "External identity disconnected{AdminTag} — {UserId} unlinked provider {ProviderId} (link {LinkId})",
            isAdmin ? " by admin" : "", user?.Id, link.LoginProviderId, link.Id);

        return Results.NoContent();
    }

    private static LinkDto ToDto(ExternalIdentityLink l, Dictionary<Guid, string> providerByName)
    {
        System.Text.Json.JsonElement? scriptOutput = l.LastScriptOutput is null ? null
            : System.Text.Json.JsonDocument.Parse(l.LastScriptOutput.RootElement.GetRawText()).RootElement;
        System.Text.Json.JsonElement? rawClaims = l.LastRawClaims is null ? null
            : System.Text.Json.JsonDocument.Parse(l.LastRawClaims.RootElement.GetRawText()).RootElement;

        return new LinkDto(
            Id: new ShortGuid(l.Id).ToString(),
            LoginProviderId: new ShortGuid(l.LoginProviderId).ToString(),
            ProviderDisplayName: providerByName.TryGetValue(l.LoginProviderId, out var n) ? n : l.Issuer,
            Issuer: l.Issuer,
            Email: l.Email,
            DisplayName: l.DisplayName,
            LinkedAt: l.LinkedAt,
            LastLoginAt: l.LastLoginAt,
            LastCapturedAt: l.LastCapturedAt == default ? null : l.LastCapturedAt,
            LastScriptSucceeded: l.LastScriptSucceeded,
            LastScriptError: l.LastScriptError,
            LastScriptOutput: scriptOutput,
            LastRawClaims: rawClaims);
    }

    /// <summary>
    /// Link view with the latest user-update-script snapshot inlined for the
    /// admin debugging modal. <see cref="LastScriptOutput"/> is the raw JSON
    /// object the script returned; <see cref="LastRawClaims"/> is the IdP's
    /// claim payload pre-script (only populated when <c>StoreRawClaims</c> is
    /// on).
    /// </summary>
    public record LinkDto(
        string Id,
        string LoginProviderId,
        string ProviderDisplayName,
        string Issuer,
        string? Email,
        string? DisplayName,
        DateTimeOffset LinkedAt,
        DateTimeOffset LastLoginAt,
        DateTimeOffset? LastCapturedAt,
        bool LastScriptSucceeded,
        string? LastScriptError,
        System.Text.Json.JsonElement? LastScriptOutput,
        System.Text.Json.JsonElement? LastRawClaims);
}
