using BuildingBlocks.Helper;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Modgud.Api.Features.Auth.PositionTerminals;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authorization.AspNetCore;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.PositionTerminals;
using Modgud.Infrastructure.OpenIddict.Dpop;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.Staffing;

/// <summary>
/// MG-FT-05 (plan §12) — the staffing-ceremony BEGIN endpoint: an enrolled
/// terminal, holding its enrollment access token, asks for WebAuthn assertion
/// options so a person can tap their passkey and open a
/// <see cref="StaffingSession"/> (redeemed via the custom staffing grant at
/// the token endpoint, §13). Position and terminal are derived from the
/// validated token — the request chooses nothing.
///
/// <para>Contract note (documented deviation from §12's
/// <c>Authorization: DPoP</c> sketch): the API's OpenIddict validation
/// pipeline extracts Bearer tokens only, so the enrollment token travels as
/// <c>Authorization: Bearer</c> while the DPoP proof-of-possession is
/// enforced explicitly here — the <c>DPoP</c> header is mandatory and its
/// key must be the slot's enrolled key (§12.2 check 7). Full
/// DPoP-scheme-extraction on the resource side is a hardening follow-up.</para>
/// </summary>
public static class StaffingEndpoints
{
    public static WebApplication MapStaffingEndpoints(this WebApplication application)
    {
        application.MapPost("/connect/staffing/begin", BeginAsync)
            .WithName("Staffing_Begin")
            .WithTags("Position Staffing")
            .DisableAntiforgery()
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            });

        // §15.2 — the terminal-facing local lock. Authenticated by either
        // position token of the SAME terminal (staffing token of the active
        // session, or the enrollment token so locking works with an expired
        // access token); DPoP-proofed with the slot's enrolled key. Path note:
        // under /connect like the other terminal-control surfaces (the plan
        // sketch shows it root-level).
        application.MapPost("/connect/staffing/{terminalId:guid}/lock", LockAsync)
            .WithName("Staffing_Lock")
            .WithTags("Position Staffing")
            .DisableAntiforgery()
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            });

        // §15.3 — admin surface (cookie-auth like every /api endpoint).
        var admin = application.MapGroup("/api")
            .WithTags("Position Staffing")
            .RequireAuthorization();

        admin.MapGet("position/{positionId}/staffing-sessions", ListSessionsAsync)
            .WithName("V2_StaffingSessions_List")
            .RequiresPermission("staffing-session:read");

        admin.MapPost("staffing-session/{sessionId}/force-lock", (
                ShortGuid sessionId, AppSettings settings, IStaffingRevoker revoker, CancellationToken ct) =>
                ForceLockAsync(settings, () => revoker.EndSessionAsync(
                    sessionId.Guid, StaffingSessionEndReason.RemoteLock, ct)))
            .WithName("V2_StaffingSessions_ForceLock")
            .RequiresPermission("staffing-session:force-lock");

        admin.MapPost("position-terminal/{terminalId}/force-lock", (
                ShortGuid terminalId, AppSettings settings, IStaffingRevoker revoker, CancellationToken ct) =>
                ForceLockAsync(settings, () => revoker.EndAllForTerminalAsync(
                    terminalId.Guid, StaffingSessionEndReason.RemoteLock, ct)))
            .WithName("V2_Terminals_ForceLock")
            .RequiresPermission("staffing-session:force-lock");

        return application;
    }

    private static async Task<IResult> ForceLockAsync(AppSettings settings, Func<Task<int>> end)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        var ended = await end();
        // Idempotent — force-locking an idle terminal / ended session is a
        // successful no-op, not an error.
        return Results.Ok(new { Ended = ended });
    }

    private static async Task<IResult> ListSessionsAsync(
        ShortGuid positionId,
        AppSettings settings,
        IDocumentSession session,
        CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        if (await session.LoadAsync<PositionPrincipal>(positionId.Guid, ct) is not { IsDeleted: false })
            return Results.NotFound();

        var sessions = await session.Query<StaffingSession>()
            .Where(s => s.PositionPrincipalId == positionId.Guid)
            .OrderByDescending(s => s.StartedAt)
            .Take(100)
            .ToListAsync(ct);

        // ActivatedBy* stays admin-only security metadata (plan §4.5) — it is
        // shown HERE for audit purposes and never travels in tokens.
        return Results.Ok(sessions.Select(s => new
        {
            Id = new ShortGuid(s.Id).ToString(),
            TerminalId = new ShortGuid(s.TerminalEnrollmentId).ToString(),
            ActivatedByUserId = new ShortGuid(s.ActivatedByUserId).ToString(),
            s.Status,
            s.StartedAt,
            s.AbsoluteExpiresAt,
            s.EndedAt,
            s.EndReason,
        }));
    }

    private static async Task<IResult> LockAsync(
        Guid terminalId,
        HttpContext context,
        AppSettings settings,
        IDocumentSession session,
        IStaffingRevoker revoker,
        CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();

        var principal = context.User;
        var tokenUse = principal.GetClaim(PositionTokenClaimTypes.TokenUse);
        var isStaffing = string.Equals(tokenUse, PositionTokenUses.StaffingSession, StringComparison.Ordinal);
        var isEnrollment = string.Equals(tokenUse, PositionTokenUses.TerminalEnrollment, StringComparison.Ordinal);
        if (!isStaffing && !isEnrollment)
            return Forbidden("Staffing.InvalidToken", "A position terminal token is required.");

        // Own terminal only — the token's terminal claim must be the route's.
        if (!Guid.TryParse(principal.GetClaim(PositionTokenClaimTypes.TerminalId), out var tokenTerminalId) ||
            tokenTerminalId != terminalId)
        {
            return Forbidden("Staffing.ForeignTerminal", "The token does not belong to this terminal.");
        }

        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        if (terminal is null || string.IsNullOrEmpty(terminal.DpopJkt))
            return Forbidden("Staffing.InvalidToken", "A position terminal token is required.");

        // Same device key — a lock from another machine is not a local lock.
        var proofHeader = context.Request.Headers[DpopConstants.HeaderName];
        if (proofHeader.Count != 1)
            return Forbidden("Staffing.DpopRequired", "A DPoP proof is required.");
        var htu = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
        var proof = DpopProofValidator.Validate(
            proofHeader.ToString(), context.Request.Method, htu, DateTimeOffset.UtcNow);
        if (!proof.IsValid || !string.Equals(proof.Jkt, terminal.DpopJkt, StringComparison.Ordinal))
            return Forbidden("Staffing.DpopMismatch", "The DPoP proof key is not this terminal's enrolled key.");

        // A staffing token may only lock ITS OWN (still-active) session — a
        // superseded shift's token cannot kill the successor.
        if (isStaffing)
        {
            if (!Guid.TryParse(principal.GetClaim(PositionTokenClaimTypes.StaffingSessionId), out var tokenSessionId) ||
                terminal.ActiveStaffingSessionId != tokenSessionId)
            {
                return Forbidden("Staffing.SessionSuperseded", "The staffing session is no longer this terminal's active session.");
            }
        }

        var ended = await revoker.EndAllForTerminalAsync(terminalId, StaffingSessionEndReason.LocalLock, ct);
        return Results.Ok(new { Ended = ended });
    }

    private static async Task<IResult> BeginAsync(
        HttpContext context,
        AppSettings settings,
        IDocumentSession session,
        RealmScopedFido2Factory fido2Factory,
        RpIdResolver rpIdResolver,
        CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();

        // §12.2 checks 1+2 — only a terminal-enrollment token scoped to the
        // terminal-control audience may begin a ceremony.
        var principal = context.User;
        if (!string.Equals(principal.GetClaim(PositionTokenClaimTypes.TokenUse),
                PositionTokenUses.TerminalEnrollment, StringComparison.Ordinal) ||
            !principal.GetAudiences().Contains(PositionTerminalControl.Audience))
        {
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");
        }

        if (!Guid.TryParse(principal.GetClaim(Claims.Subject), out var positionId) ||
            !Guid.TryParse(principal.GetClaim(PositionTokenClaimTypes.TerminalId), out var terminalId))
        {
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");
        }

        // §12.2 checks 3+4 — the slot exists and its client link is intact in
        // both directions; the token's client (when stamped) must be the
        // slot's own client.
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        if (terminal is null || terminal.PositionPrincipalId != positionId)
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");

        var state = await session.LoadAsync<OAuthApplicationState>(terminal.OAuthApplicationId, ct);
        if (state is null || state.IsDeleted || state.ManagedTerminalEnrollmentId != terminal.Id)
            return Forbidden("Staffing.ClientLinkBroken", "The terminal's OAuth client link is not intact.");

        var tokenClientId = principal.GetClaim(Claims.ClientId) ?? principal.GetClaim(Claims.AuthorizedParty);
        if (tokenClientId is not null && !string.Equals(tokenClientId, terminal.ClientId, StringComparison.Ordinal))
            return Forbidden("Staffing.ClientMismatch", "The token was not issued to this terminal's client.");

        // §12.2 checks 5+6 — position alive + policy on, slot Active.
        var position = await session.LoadAsync<PositionPrincipal>(positionId, ct);
        if (position is null || position.IsDeleted || !position.TerminalPolicy.Enabled)
            return Forbidden("Staffing.PositionUnavailable", "Terminal use is disabled for this position.");
        if (terminal.Status != TerminalEnrollmentStatus.Active)
            return Forbidden("Staffing.TerminalNotActive", "The terminal is not active.");

        // §12.2 check 7 — proof-of-possession: the DPoP proof presented with
        // THIS request must be signed by the slot's enrolled key.
        var proofHeader = context.Request.Headers[DpopConstants.HeaderName];
        if (proofHeader.Count != 1)
            return Forbidden("Staffing.DpopRequired", "A DPoP proof is required.");
        var htu = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
        var proof = DpopProofValidator.Validate(
            proofHeader.ToString(), context.Request.Method, htu, DateTimeOffset.UtcNow);
        if (!proof.IsValid || !string.Equals(proof.Jkt, terminal.DpopJkt, StringComparison.Ordinal))
            return Forbidden("Staffing.DpopMismatch", "The DPoP proof key is not this terminal's enrolled key.");

        // §12.2 check 8 — at least one ACTIVE user→position grant (Suspended
        // does not authorize staffing).
        var grantedUserIds = (await session.Query<PositionGrant>()
            .Where(g => g.PositionPrincipalId == positionId && g.Status == PositionGrantStatus.Active)
            .ToListAsync(ct))
            .Select(g => g.UserId)
            .Distinct()
            .ToList();
        if (grantedUserIds.Count == 0)
            return Forbidden("Staffing.NoActiveGrants", "No user is authorized to staff this position.");

        // §12.2 check 9 + §12.3 — allowCredentials restricted to the granted
        // users' passkeys under the terminal's RP-ID (legacy RpId == null
        // credentials count for the realm's primary domain).
        var primaryDomain = await rpIdResolver.GetPrimaryDomainAsync(ct);
        var allowedCredentials = (await session.Query<StoredPasskeyCredential>()
            .Where(c => grantedUserIds.Contains(c.UserId))
            .ToListAsync(ct))
            .Where(c => string.Equals(c.RpId ?? primaryDomain, terminal.WebAuthnRpId, StringComparison.OrdinalIgnoreCase))
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToList();
        if (allowedCredentials.Count == 0)
            return Forbidden("Staffing.NoEligiblePasskeys", "No authorized user has a passkey for this terminal.");

        IFido2 fido2;
        try
        {
            fido2 = await fido2Factory.CreateAsync(ct, rpIdOverride: terminal.WebAuthnRpId);
        }
        catch (RelyingPartyUnavailableException)
        {
            return Forbidden("Staffing.RelyingPartyUnavailable", "The terminal's relying party is not available.");
        }

        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowedCredentials,
            UserVerification = UserVerificationRequirement.Preferred,
        });
        var optionsJson = options.ToJson();

        // Amortized cleanup of lapsed ceremonies (plan §5.3), then persist the
        // pinned ceremony — the token request may re-choose NOTHING of this.
        session.DeleteWhere<StaffingCeremony>(c => c.ExpiresAt < DateTimeOffset.UtcNow);
        var ceremony = new StaffingCeremony
        {
            Id = Guid.NewGuid(),
            PositionPrincipalId = position.Id,
            TerminalEnrollmentId = terminal.Id,
            ClientId = terminal.ClientId,
            DpopJkt = terminal.DpopJkt!,
            RpId = terminal.WebAuthnRpId,
            OptionsJson = optionsJson,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);

        // Options JSON must reach the authenticator verbatim (same rationale as
        // the native passkey begin) — no re-serialization.
        return Results.Content(
            $"{{\"ceremonyId\":\"{ceremony.Id}\",\"publicKey\":{optionsJson}}}",
            "application/json");
    }

    private static IResult Forbidden(string error, string message) =>
        Results.Json(new { Error = error, Message = message }, statusCode: StatusCodes.Status403Forbidden);
}
