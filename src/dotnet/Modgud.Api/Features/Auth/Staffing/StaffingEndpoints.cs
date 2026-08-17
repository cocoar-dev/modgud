using BuildingBlocks.Helper;
using System.Text.Json;
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
using RealmSettingsDoc = Modgud.Domain.RealmSettings.RealmSettings;

namespace Modgud.Api.Features.Auth.Staffing;

/// <summary>
/// MG-FT-05 (plan §12) — the staffing-ceremony BEGIN endpoint: an enrolled
/// terminal, holding its enrollment access token, asks for WebAuthn assertion
/// options so a person can tap their passkey and open a
/// <see cref="StaffingSession"/> (redeemed via the custom staffing grant at
/// the token endpoint, §13). The validated control token fixes the terminal;
/// V1 also fixes its singleton position, while V2 resolves an allowed position
/// only after the activation proof has succeeded.
///
/// <para>Contract note (documented deviation from §12's
/// <c>Authorization: DPoP</c> sketch): the API's OpenIddict validation
/// pipeline extracts Bearer tokens only, so the enrollment token travels as
/// <c>Authorization: Bearer</c>. DPoP-bound slots additionally enforce the
/// proof explicitly here; ClientSecret and None slots rely on their respective
/// enrollment/token-endpoint profile. Full DPoP-scheme extraction on the
/// resource side remains a hardening follow-up.</para>
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
            })
            .RequireRateLimiting("passkey-begin");

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

        application.MapPost("/connect/staffing/{terminalId:guid}/step-up", StepUpBeginAsync)
            .WithName("Staffing_StepUpBegin")
            .WithTags("Position Staffing")
            .DisableAntiforgery()
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            })
            .RequireRateLimiting("passkey-begin");

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

    private static async Task<IResult> StepUpBeginAsync(
        Guid terminalId,
        StepUpBeginInput input,
        HttpContext context,
        AppSettings settings,
        IDocumentSession session,
        ActivationProofRegistry activationProofs,
        IDpopReplayStore replayStore,
        CancellationToken ct)
    {
        if (!settings.Features.PositionTerminals) return Results.NotFound();
        var principal = context.User;
        if (!string.Equals(principal.GetClaim(PositionTokenClaimTypes.TokenUse),
                PositionTokenUses.StaffingSession, StringComparison.Ordinal) ||
            !Guid.TryParse(principal.GetClaim(Claims.Subject), out var positionId) ||
            !Guid.TryParse(principal.GetClaim(PositionTokenClaimTypes.TerminalId), out var tokenTerminalId) ||
            tokenTerminalId != terminalId ||
            !Guid.TryParse(principal.GetClaim(PositionTokenClaimTypes.StaffingSessionId), out var staffingSessionId))
            return Forbidden("Staffing.InvalidToken", "An active staffing access token is required.");

        if ((input.Action is null) != (input.Nonce is null) ||
            input.Action is { Length: > 200 } || input.Nonce is { Length: > 200 } ||
            string.IsNullOrWhiteSpace(input.Action) != string.IsNullOrWhiteSpace(input.Nonce))
            return Results.BadRequest(new
            {
                Error = "Staffing.InvalidStepUpBinding",
                Message = "Action and nonce must be supplied together and be at most 200 characters."
            });

        var staffing = await session.LoadAsync<StaffingSession>(staffingSessionId, ct);
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        var position = await session.LoadAsync<PositionPrincipal>(positionId, ct);
        if (staffing is not { Status: StaffingSessionStatus.Active } ||
            staffing.AbsoluteExpiresAt <= DateTimeOffset.UtcNow ||
            staffing.TerminalEnrollmentId != terminalId || staffing.PositionPrincipalId != positionId ||
            terminal is not { Status: TerminalEnrollmentStatus.Active } ||
            terminal.ActiveStaffingSessionId != staffing.Id ||
            !terminal.EffectiveAllowedPositionIds.Contains(positionId) ||
            position is null || position.IsDeleted || !position.IsActive || !position.TerminalPolicy.Enabled)
            return Forbidden("Staffing.SessionUnavailable", "The staffing session is no longer active.");

        if (string.Equals(terminal.Binding, DeviceBindingIds.Dpop, StringComparison.Ordinal))
        {
            var proofHeader = context.Request.Headers[DpopConstants.HeaderName];
            if (proofHeader.Count != 1 || !TryGetPresentedAccessToken(context.Request, out var accessToken))
                return Forbidden("Staffing.DpopRequired", "A DPoP proof bound to the staffing access token is required.");
            var now = DateTimeOffset.UtcNow;
            var htu = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
            var proof = DpopProofValidator.Validate(
                proofHeader.ToString(), context.Request.Method, htu, now, accessToken);
            if (!proof.IsValid || !string.Equals(proof.Jkt, terminal.DpopJkt, StringComparison.Ordinal))
                return Forbidden("Staffing.DpopMismatch", "The DPoP proof key does not match this terminal.");
            if (!await RecordDpopProofAsync(replayStore, proof, now, ct))
                return Forbidden("Staffing.DpopReplay", "The DPoP proof has already been used.");
        }

        var realm = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var requiredProof = realm?.PositionSecurity?.RequiredProofCapabilities ?? ProofCapability.None;
        var methodId = string.IsNullOrWhiteSpace(input.MethodId)
            ? position.TerminalPolicy.AllowedActivationProofs.FirstOrDefault(m =>
                PositionTerminalSecurity.ProofMeetsFloor(m, requiredProof) && activationProofs.TryGet(m, out _))
            : input.MethodId.Trim();
        if (methodId is null ||
            !position.TerminalPolicy.AllowedActivationProofs.Contains(methodId, StringComparer.Ordinal) ||
            !PositionTerminalSecurity.ProofMeetsFloor(methodId, requiredProof) ||
            !activationProofs.TryGet(methodId, out var activationProof))
            return Forbidden("Staffing.ActivationProofUnavailable", "The requested step-up proof is unavailable.");

        var challenge = await activationProof.BeginAsync(
            new ActivationContext(position, terminal,
                BeginInput: new ActivationBeginInput(methodId, input.AccountName, new ShortGuid(position.Id).ToString())), ct);
        if (challenge.Failure is { } failure) return Forbidden(failure.Code, failure.Message);
        var ceremony = challenge.Ceremony!;
        ceremony.StepUpForStaffingSessionId = staffing.Id;
        ceremony.StepUpAction = string.IsNullOrWhiteSpace(input.Action) ? null : input.Action;
        ceremony.StepUpNonce = string.IsNullOrWhiteSpace(input.Nonce) ? null : input.Nonce;
        ceremony.StepUpScopes = principal.GetScopes()
            .Where(scope => !string.Equals(scope, Scopes.OfflineAccess, StringComparison.Ordinal))
            .ToArray();
        session.Store(ceremony);
        await session.SaveChangesAsync(ct);

        return Results.Content(
            $"{{\"ceremonyId\":\"{ceremony.Id}\",\"methodId\":{JsonSerializer.Serialize(methodId)}," +
            $"\"{challenge.ResponseProperty}\":{challenge.OptionsJson}}}", "application/json");
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
        return Results.Ok(sessions.Select(s =>
        {
            var evidence = s.GetActivationEvidence();
            return new
            {
                Id = new ShortGuid(s.Id).ToString(),
                TerminalId = new ShortGuid(s.TerminalEnrollmentId).ToString(),
                ActivatedByUserId = evidence.UserId is { } userId
                    ? new ShortGuid(userId).ToString()
                    : null,
                ActivationProof = evidence.MethodId,
                ActivationTokenId = evidence.ActivationTokenId is { } tokenId
                    ? new ShortGuid(tokenId).ToString()
                    : null,
                s.Status,
                s.StartedAt,
                s.AbsoluteExpiresAt,
                s.EndedAt,
                s.EndReason,
            };
        }));
    }

    private static async Task<IResult> LockAsync(
        Guid terminalId,
        HttpContext context,
        AppSettings settings,
        IDocumentSession session,
        IDpopReplayStore replayStore,
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
        if (terminal is null)
            return Forbidden("Staffing.InvalidToken", "A position terminal token is required.");

        if (string.Equals(terminal.Binding, DeviceBindingIds.Dpop, StringComparison.Ordinal))
        {
            // Same device key — a lock from another machine is not a local lock.
            var proofHeader = context.Request.Headers[DpopConstants.HeaderName];
            if (proofHeader.Count != 1)
                return Forbidden("Staffing.DpopRequired", "A DPoP proof is required.");
            if (!TryGetPresentedAccessToken(context.Request, out var accessToken))
                return Forbidden("Staffing.InvalidToken", "The presented access token cannot be bound to the DPoP proof.");
            var now = DateTimeOffset.UtcNow;
            var htu = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
            var proof = DpopProofValidator.Validate(
                proofHeader.ToString(), context.Request.Method, htu, now, accessToken);
            if (!proof.IsValid || !string.Equals(proof.Jkt, terminal.DpopJkt, StringComparison.Ordinal))
                return Forbidden("Staffing.DpopMismatch", "The DPoP proof key is not this terminal's enrolled key.");
            if (!await RecordDpopProofAsync(replayStore, proof, now, ct))
                return Forbidden("Staffing.DpopReplay", "The DPoP proof has already been used.");
        }

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
        ActivationProofRegistry activationProofs,
        IDpopReplayStore replayStore,
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

        var isControlV2 = string.Equals(
            principal.GetClaim(PositionTokenClaimTypes.PrincipalType),
            PositionPrincipalTypes.Terminal,
            StringComparison.Ordinal);
        if (!Guid.TryParse(principal.GetClaim(Claims.Subject), out var subjectId) ||
            !Guid.TryParse(principal.GetClaim(PositionTokenClaimTypes.TerminalId), out var terminalId))
        {
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");
        }

        // §12.2 checks 3+4 — the slot exists and its client link is intact in
        // both directions; the token's client (when stamped) must be the
        // slot's own client.
        if (isControlV2) terminalId = subjectId;
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        if (terminal is null)
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");
        if (!isControlV2 &&
            (terminal.EffectiveAllowedPositionIds.Count != 1 || terminal.EffectiveAllowedPositionIds[0] != subjectId))
            return Forbidden("Staffing.LegacyControlToken", "This terminal assignment requires a V2 control token and re-enrollment.");

        var state = await session.LoadAsync<OAuthApplicationState>(terminal.OAuthApplicationId, ct);
        if (state is null || state.IsDeleted || state.ManagedTerminalEnrollmentId != terminal.Id)
            return Forbidden("Staffing.ClientLinkBroken", "The terminal's OAuth client link is not intact.");

        var tokenClientId = principal.GetClaim(Claims.ClientId) ?? principal.GetClaim(Claims.AuthorizedParty);
        if (tokenClientId is not null && !string.Equals(tokenClientId, terminal.ClientId, StringComparison.Ordinal))
            return Forbidden("Staffing.ClientMismatch", "The token was not issued to this terminal's client.");

        // §12.2 check 6 — the slot itself must be active before either listing
        // V2 candidates or beginning a proof.
        if (terminal.Status != TerminalEnrollmentStatus.Active)
            return Forbidden("Staffing.TerminalNotActive", "The terminal is not active.");

        // Sender constraint protects candidate discovery as well as ceremony
        // creation. Other bindings rely on their token-endpoint client mode.
        if (string.Equals(terminal.Binding, DeviceBindingIds.Dpop, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(terminal.DpopJkt))
                return Forbidden("Staffing.BindingUnavailable", "The terminal has no enrolled device key.");
            var proofHeader = context.Request.Headers[DpopConstants.HeaderName];
            if (proofHeader.Count != 1)
                return Forbidden("Staffing.DpopRequired", "A DPoP proof is required.");
            if (!TryGetPresentedAccessToken(context.Request, out var accessToken))
                return Forbidden("Staffing.InvalidToken", "The presented access token cannot be bound to the DPoP proof.");
            var now = DateTimeOffset.UtcNow;
            var htu = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
            var proof = DpopProofValidator.Validate(
                proofHeader.ToString(), context.Request.Method, htu, now, accessToken);
            if (!proof.IsValid || !string.Equals(proof.Jkt, terminal.DpopJkt, StringComparison.Ordinal))
                return Forbidden("Staffing.DpopMismatch", "The DPoP proof key is not this terminal's enrolled key.");
            if (!await RecordDpopProofAsync(replayStore, proof, now, ct))
                return Forbidden("Staffing.DpopReplay", "The DPoP proof has already been used.");
        }

        ActivationBeginInput beginInput;
        try
        {
            beginInput = context.Request.ContentLength is > 0
                ? await JsonSerializer.DeserializeAsync<ActivationBeginInput>(
                      context.Request.Body,
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                  ?? new ActivationBeginInput(null, null)
                : new ActivationBeginInput(null, null);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new
            {
                Error = "Staffing.InvalidBeginRequest",
                Message = "The staffing begin request is not valid JSON."
            });
        }

        var realm = await session.LoadAsync<RealmSettingsDoc>(RealmSettingsDoc.SingletonId, ct);
        var requiredProof = realm?.PositionSecurity?.RequiredProofCapabilities ?? ProofCapability.None;
        var requiredBinding = realm?.PositionSecurity?.RequiredBindingCapabilities ?? BindingCapability.None;
        if (!PositionTerminalSecurity.BindingMeetsFloor(terminal.Binding, requiredBinding))
            return Forbidden("Staffing.BindingBelowRealmFloor", "The terminal binding does not meet the realm security floor.");

        Guid positionId;
        if (isControlV2)
        {
            if (string.IsNullOrWhiteSpace(beginInput.PositionId))
            {
                var candidates = new List<PositionPrincipal>();
                foreach (var allowedId in terminal.EffectiveAllowedPositionIds)
                {
                    var candidate = await session.LoadAsync<PositionPrincipal>(allowedId, ct);
                    if (candidate is null || candidate.IsDeleted || !candidate.IsActive ||
                        !candidate.TerminalPolicy.Enabled ||
                        !candidate.TerminalPolicy.AllowedDeviceBindings.Contains(terminal.Binding, StringComparer.Ordinal))
                        continue;
                    candidates.Add(candidate);
                }

                var requestedMethod = beginInput.MethodId?.Trim();
                IActivationProof? candidateProof = null;
                if (!string.IsNullOrWhiteSpace(requestedMethod))
                {
                    if (!PositionTerminalSecurity.ProofMeetsFloor(requestedMethod, requiredProof) ||
                        !activationProofs.TryGet(requestedMethod, out candidateProof))
                        return Forbidden("Staffing.ActivationProofUnavailable",
                            "The requested activation proof is not currently available.");
                }
                else
                {
                    requestedMethod = candidates
                        .SelectMany(candidate => candidate.TerminalPolicy.AllowedActivationProofs)
                        .FirstOrDefault(method =>
                            PositionTerminalSecurity.ProofMeetsFloor(method, requiredProof) &&
                            activationProofs.TryGet(method, out _));
                    if (requestedMethod is not null)
                        activationProofs.TryGet(requestedMethod, out candidateProof);
                }
                if (candidateProof is null || requestedMethod is null)
                    return Forbidden("Staffing.ActivationProofUnavailable",
                        "No allowed activation proof is currently available.");

                candidates = candidates
                    .Where(candidate => candidate.TerminalPolicy.AllowedActivationProofs
                        .Contains(requestedMethod, StringComparer.Ordinal))
                    .ToList();
                if (candidates.Count == 0)
                    return Forbidden("Staffing.ActivationProofNotAllowed",
                        "The requested activation proof is not allowed on this terminal.");

                if (candidates.Count > 1)
                {
                    // Proof first: the challenge contains the union of eligible
                    // credentials, but position names/ids stay server-side. The
                    // redeem identifies the actor/token and only then returns
                    // the intersected candidates when an actual choice remains.
                    var candidateChallenge = await candidateProof.BeginCandidatesAsync(
                        candidates, terminal, beginInput with { MethodId = requestedMethod }, ct);
                    if (candidateChallenge.Failure is { } candidateFailure)
                        return Forbidden(candidateFailure.Code, candidateFailure.Message);
                    return ChallengeResult(candidateProof, candidateChallenge);
                }
                positionId = candidates[0].Id;
            }
            else if (terminal.EffectiveAllowedPositionIds.Count > 1)
                return Forbidden("Staffing.ProofRequiredBeforeSelection",
                    "A multi-position terminal can select a position only after activation proof verification.");
            else if (!ShortGuid.TryParse(beginInput.PositionId, out positionId) ||
                     !terminal.EffectiveAllowedPositionIds.Contains(positionId))
                return Forbidden("Staffing.PositionNotAllowed", "The selected position is not allowed on this terminal.");
        }
        else
        {
            positionId = subjectId;
            if (!string.IsNullOrWhiteSpace(beginInput.PositionId) &&
                (!ShortGuid.TryParse(beginInput.PositionId, out Guid requestedId) || requestedId != positionId))
                return Forbidden("Staffing.PositionNotAllowed", "A legacy control token cannot select another position.");
        }

        // §12.2 check 5 — selected position alive + compatible policy.
        var position = await session.LoadAsync<PositionPrincipal>(positionId, ct);
        if (position is null || position.IsDeleted || !position.IsActive || !position.TerminalPolicy.Enabled)
            return Forbidden("Staffing.PositionUnavailable", "Terminal use is disabled for this position.");

        // Current policy and realm floors are checked at execution time as the
        // fail-closed half of read-preserve/write-reject for open IDs.
        if (!position.TerminalPolicy.AllowedDeviceBindings.Contains(terminal.Binding, StringComparer.Ordinal))
            return Forbidden("Staffing.BindingNotAllowed", "The terminal binding is no longer allowed by this position.");
        IActivationProof? activationProof = null;
        if (!string.IsNullOrWhiteSpace(beginInput.MethodId))
        {
            var requestedMethod = beginInput.MethodId.Trim();
            if (!position.TerminalPolicy.AllowedActivationProofs.Contains(requestedMethod, StringComparer.Ordinal))
                return Forbidden("Staffing.ActivationProofNotAllowed", "The requested activation proof is not allowed by this position.");
            if (!PositionTerminalSecurity.ProofMeetsFloor(requestedMethod, requiredProof) ||
                !activationProofs.TryGet(requestedMethod, out activationProof))
                return Forbidden("Staffing.ActivationProofUnavailable", "The requested activation proof is not currently available.");
        }
        else foreach (var methodId in position.TerminalPolicy.AllowedActivationProofs)
        {
            if (PositionTerminalSecurity.ProofMeetsFloor(methodId, requiredProof) &&
                activationProofs.TryGet(methodId, out activationProof))
                break;
            activationProof = null;
        }
        if (activationProof is null)
            return Forbidden("Staffing.ActivationProofUnavailable", "No allowed activation proof is currently available.");

        var challenge = await activationProof.BeginAsync(
            new ActivationContext(position, terminal, BeginInput: beginInput), ct);
        if (challenge.Failure is { } failure)
            return Forbidden(failure.Code, failure.Message);
        return ChallengeResult(activationProof, challenge);
    }

    private static IResult ChallengeResult(IActivationProof proof, ActivationChallenge challenge)
    {
        var ceremony = challenge.Ceremony!;
        // Options JSON must reach the authenticator verbatim (same rationale as
        // the native passkey begin) — no re-serialization.
        return Results.Content(
            $"{{\"ceremonyId\":\"{ceremony.Id}\",\"methodId\":{JsonSerializer.Serialize(proof.MethodId)}," +
            $"\"{challenge.ResponseProperty}\":{challenge.OptionsJson}}}",
            "application/json");
    }

    private static IResult Forbidden(string error, string message) =>
        Results.Json(new { Error = error, Message = message }, statusCode: StatusCodes.Status403Forbidden);

    private static bool TryGetPresentedAccessToken(HttpRequest request, out string accessToken)
    {
        accessToken = string.Empty;
        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1) return false;
        var raw = authorization.ToString();
        var separator = raw.IndexOf(' ');
        if (separator <= 0 || separator == raw.Length - 1) return false;
        if (!raw.AsSpan(0, separator).Equals("Bearer", StringComparison.OrdinalIgnoreCase) &&
            !raw.AsSpan(0, separator).Equals("DPoP", StringComparison.OrdinalIgnoreCase))
            return false;
        accessToken = raw[(separator + 1)..].Trim();
        return accessToken.Length > 0;
    }

    private static Task<bool> RecordDpopProofAsync(
        IDpopReplayStore replayStore,
        DpopValidationResult proof,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var expiresAt = proof.IssuedAt!.Value
            + DpopProofValidator.DefaultMaxAge
            + DpopProofValidator.DefaultClockSkew;
        return replayStore.TryRecordAsync(proof.Jti!, expiresAt, now, ct);
    }
}

public sealed record StepUpBeginInput(
    string? MethodId,
    string? AccountName,
    string? Action,
    string? Nonce);
