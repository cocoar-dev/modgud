using System.Security.Claims;
using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.SelfRegistration;
using Modgud.Authentication.SelfRegistration.Domain;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.InviteCodes;

/// <summary>
/// ADR-0012 — app-scoped admin surface for single-use registration invite codes
/// (the <c>InviteCode</c> posture). Dual-auth (D9): the M2M leg authorizes with
/// the app-bound <c>invite:write</c>/<c>invite:read</c> OAuth scope (a
/// ServiceAccount <c>client_credentials</c> caller — typically the consuming app's
/// backend); the admin leg authorizes with the in-process
/// <c>invite-code:write</c>/<c>invite-code:read</c> permission (the admin-UI
/// bulk-mint). Both are gated by <see cref="ScopeOrPermissionEndpointFilter"/>,
/// which also enforces that the route <c>{appId}</c> matches the caller's app.
///
/// <para>The plaintext code is returned by the mint endpoint exactly once; only
/// its hash is stored. Redemption is implicit on the native sign-up path under the
/// <c>InviteCode</c> posture — there is no redeem endpoint here.</para>
/// </summary>
public static class InviteCodeEndpoints
{
    private const string ScopeWrite = "invite:write";
    private const string ScopeRead = "invite:read";
    private const string PermissionWrite = "invite-code:write";
    private const string PermissionRead = "invite-code:read";

    public static WebApplication MapInviteCodeEndpoints(this WebApplication application, string path)
    {
        // Accept BOTH the admin cookie and a validated M2M bearer token, so
        // HttpContext.User is populated for either caller; the per-endpoint
        // ScopeOrPermissionEndpointFilter then decides which grant applies.
        var dualScheme = $"{IdentityConstants.ApplicationScheme}," +
                         $"{OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme}";

        var group = application.MapGroup($"{path}/app/{{appId}}/invite-codes")
            .WithTags("Invite Codes")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = dualScheme })
            // M2M callers send a bearer token, not the SPA's antiforgery cookie pair.
            .DisableAntiforgery();

        // POST — mint N codes for {appId}. Returns plaintext once.
        group.MapPost("", async (
                string appId,
                MintInviteCodesDto dto,
                HttpContext httpContext,
                IRegistrationInviteService inviteService,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                CancellationToken ct) =>
            {
                if (!ShortGuid.TryParse(appId, out Guid appGuid))
                    return Results.BadRequest(new { Message = "Invalid appId." });

                var subject = ResolveSubject(httpContext.User);
                var codes = await inviteService.MintAsync(
                    appGuid, dto.BoundEmail, dto.ExpiresInDays, subject,
                    dto.Count < 1 ? 1 : dto.Count, ct);

                // Live-refresh the admin grid (the list view subscribes to the
                // "InviteCode" change stream via InviteCodeHub and reloads when the
                // event's AppId matches the app it is showing). Only the AppId + count
                // cross the wire — the plaintext stays in the HTTP response to the
                // minting caller.
                dispatcher.DispatchCreatedEvent("InviteCode", new { AppId = appId, Count = codes.Count }, session.TenantId);
                return Results.Ok(new MintInviteCodesResultDto(codes));
            })
            .WithName("InviteCodes_Mint")
            .AddEndpointFilter(new ScopeOrPermissionEndpointFilter(ScopeWrite, PermissionWrite));

        // GET — list open/used/expired codes for {appId} (metadata only).
        group.MapGet("", async (
                string appId,
                IRegistrationInviteService inviteService,
                CancellationToken ct) =>
            {
                if (!ShortGuid.TryParse(appId, out Guid appGuid))
                    return Results.BadRequest(new { Message = "Invalid appId." });

                var codes = await inviteService.ListAsync(appGuid, ct);
                return Results.Ok(codes.Select(ToDto));
            })
            .WithName("InviteCodes_List")
            .AddEndpointFilter(new ScopeOrPermissionEndpointFilter(ScopeRead, PermissionRead));

        // DELETE — revoke an unused code before it is redeemed.
        group.MapDelete("{id}", async (
                string appId,
                string id,
                IRegistrationInviteService inviteService,
                IDocumentSession session,
                DataEventDispatcher dispatcher,
                CancellationToken ct) =>
            {
                if (!ShortGuid.TryParse(appId, out Guid appGuid) || !ShortGuid.TryParse(id, out Guid codeId))
                    return Results.BadRequest(new { Message = "Invalid id." });

                var revoked = await inviteService.RevokeAsync(appGuid, codeId, ct);
                if (!revoked)
                    return Results.NotFound();

                dispatcher.DispatchDeletedEvent("InviteCode", new { AppId = appId, Id = new ShortGuid(codeId).ToString() }, session.TenantId);
                return Results.NoContent();
            })
            .WithName("InviteCodes_Revoke")
            .AddEndpointFilter(new ScopeOrPermissionEndpointFilter(ScopeWrite, PermissionWrite));

        return application;
    }

    private static string ResolveSubject(ClaimsPrincipal user) =>
        user.GetClaim(Claims.Subject)
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.GetClaim(Claims.ClientId)
        ?? "unknown";

    private static InviteCodeDto ToDto(RegistrationInviteCode c) => new(
        Id: new ShortGuid(c.Id).ToString(),
        AppId: new ShortGuid(c.AppId).ToString(),
        BoundEmail: c.BoundEmail,
        CreatedAt: c.CreatedAt,
        ExpiresAt: c.ExpiresAt,
        CreatedBySubject: c.CreatedBySubject,
        UsedAt: c.UsedAt,
        UsedByUserId: c.UsedByUserId is { } uid ? new ShortGuid(uid).ToString() : null,
        Status: c.IsUsed ? "Used" : c.IsExpired ? "Expired" : "Open");
}
