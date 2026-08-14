using Fido2NetLib;
using Fido2NetLib.Objects;
using Marten;
using Microsoft.AspNetCore.Authorization;
using Modgud.Api.Features.Auth.FunctionTerminals;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Identity;
using Modgud.Authorization.Principals;
using Modgud.Domain.FunctionTerminals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.OpenIddict.Dpop;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.FunctionStaffing;

/// <summary>
/// MG-FT-05 (plan §12) — the staffing-ceremony BEGIN endpoint: an enrolled
/// terminal, holding its enrollment access token, asks for WebAuthn assertion
/// options so a person can tap their passkey and open a
/// <see cref="StaffingSession"/> (redeemed via the custom staffing grant at
/// the token endpoint, §13). Function and terminal are derived from the
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
public static class FunctionStaffingEndpoints
{
    public static WebApplication MapFunctionStaffingEndpoints(this WebApplication application)
    {
        application.MapPost("/connect/function-staffing/begin", BeginAsync)
            .WithName("FunctionStaffing_Begin")
            .WithTags("Function Staffing")
            .DisableAntiforgery()
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            });

        return application;
    }

    private static async Task<IResult> BeginAsync(
        HttpContext context,
        AppSettings settings,
        IDocumentSession session,
        RealmScopedFido2Factory fido2Factory,
        RpIdResolver rpIdResolver,
        CancellationToken ct)
    {
        if (!settings.Features.FunctionTerminals) return Results.NotFound();

        // §12.2 checks 1+2 — only a terminal-enrollment token scoped to the
        // terminal-control audience may begin a ceremony.
        var principal = context.User;
        if (!string.Equals(principal.GetClaim(FunctionTokenClaimTypes.TokenUse),
                FunctionTokenUses.TerminalEnrollment, StringComparison.Ordinal) ||
            !principal.GetAudiences().Contains(FunctionTerminalControl.Audience))
        {
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");
        }

        if (!Guid.TryParse(principal.GetClaim(Claims.Subject), out var functionId) ||
            !Guid.TryParse(principal.GetClaim(FunctionTokenClaimTypes.TerminalId), out var terminalId))
        {
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");
        }

        // §12.2 checks 3+4 — the slot exists and its client link is intact in
        // both directions; the token's client (when stamped) must be the
        // slot's own client.
        var terminal = await session.LoadAsync<TerminalEnrollment>(terminalId, ct);
        if (terminal is null || terminal.FunctionPrincipalId != functionId)
            return Forbidden("Staffing.InvalidToken", "A terminal enrollment token is required.");

        var state = await session.LoadAsync<OAuthApplicationState>(terminal.OAuthApplicationId, ct);
        if (state is null || state.IsDeleted || state.ManagedTerminalEnrollmentId != terminal.Id)
            return Forbidden("Staffing.ClientLinkBroken", "The terminal's OAuth client link is not intact.");

        var tokenClientId = principal.GetClaim(Claims.ClientId) ?? principal.GetClaim(Claims.AuthorizedParty);
        if (tokenClientId is not null && !string.Equals(tokenClientId, terminal.ClientId, StringComparison.Ordinal))
            return Forbidden("Staffing.ClientMismatch", "The token was not issued to this terminal's client.");

        // §12.2 checks 5+6 — function alive + policy on, slot Active.
        var function = await session.LoadAsync<FunctionPrincipal>(functionId, ct);
        if (function is null || function.IsDeleted || !function.TerminalPolicy.Enabled)
            return Forbidden("Staffing.FunctionUnavailable", "Terminal use is disabled for this function.");
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

        // §12.2 check 8 — at least one ACTIVE user→function grant (Suspended
        // does not authorize staffing).
        var grantedUserIds = (await session.Query<FunctionActivationGrant>()
            .Where(g => g.FunctionPrincipalId == functionId && g.Status == FunctionActivationGrantStatus.Active)
            .ToListAsync(ct))
            .Select(g => g.UserId)
            .Distinct()
            .ToList();
        if (grantedUserIds.Count == 0)
            return Forbidden("Staffing.NoActiveGrants", "No user is authorized to staff this function.");

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
        session.DeleteWhere<FunctionStaffingCeremony>(c => c.ExpiresAt < DateTimeOffset.UtcNow);
        var ceremony = new FunctionStaffingCeremony
        {
            Id = Guid.NewGuid(),
            FunctionPrincipalId = function.Id,
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
