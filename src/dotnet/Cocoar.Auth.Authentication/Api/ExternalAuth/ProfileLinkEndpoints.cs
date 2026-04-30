using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Cocoar.Auth.Authorization.AspNetCore;
using Cocoar.Auth.Authentication.ExtensionMethods;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Authentication.Domain.LoginProviders;

namespace Cocoar.Auth.Authentication.Api.ExternalAuth;

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
        .RequiresPermission("cocoar-auth:user:read");

        // Disconnect a link. Soft-delete (IsUnlinked=true) so the same external
        // identity can be re-added to a different user later if needed.
        group.MapDelete("{linkId}", async (
            ShortGuid linkId,
            HttpContext http,
            TimeProvider clock,
            [FromServices] IDocumentSession writeSession,
            [FromServices] UserManager<ApplicationUser> userManager,
            CancellationToken ct) =>
        {
            var userId = http.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var link = await writeSession.LoadAsync<ExternalIdentityLink>(linkId.Guid, ct);
            if (link is null || link.IsUnlinked) return Results.NotFound();
            if (link.UserId != userId.Value) return Results.Forbid();

            // Self-lockout guard: if this is the last auth method (no local
            // password AND no passkey AND no other external links), refuse.
            var user = await userManager.FindByIdAsync(userId.Value.ToString());
            if (user is not null)
            {
                var otherLinks = await writeSession.Query<ExternalIdentityLink>()
                    .Where(l => l.UserId == userId.Value && !l.IsUnlinked && l.Id != link.Id)
                    .AnyAsync(ct);
                var hasPassword = await userManager.HasPasswordAsync(user);
                // Passkey count check would require a dedicated store query — approximate
                // by trusting local auth fallback when a password is set.
                if (!otherLinks && !hasPassword)
                {
                    return Results.BadRequest(new
                    {
                        Code = "Idp.LastAuthMethod",
                        Message = "Cannot remove the only remaining authentication method. Set a password or add another provider first.",
                    });
                }
            }

            var now = clock.GetUtcNow();
            writeSession.Events.Append(link.Id,
                new ExternalIdentityUnlinkedEvent(link.Id, now, userId));
            writeSession.Events.Append(link.UserId,
                new UserExternalIdentityUnlinkedEvent(link.UserId, link.Id, link.LoginProviderId, now));
            await writeSession.SaveChangesAsync(ct);

            return Results.NoContent();
        });
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
