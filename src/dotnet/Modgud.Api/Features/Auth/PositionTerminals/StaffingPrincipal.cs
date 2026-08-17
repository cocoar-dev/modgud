using System.Security.Claims;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.PositionTerminals;

/// <summary>
/// Builds the claims principal for STAFFING-SESSION tokens (MG-FT-05, plan
/// §7.3): the business token a staffed terminal works with. The subject is the
/// POSITION; the activating person's identity is deliberately absent (security
/// metadata lives on the <see cref="StaffingSession"/> document only —
/// consuming systems must never see who tapped). <c>amr=webauthn</c> and
/// <c>auth_time</c> document HOW and WHEN the shift was opened.
/// </summary>
public static class StaffingPrincipal
{
    public static ClaimsPrincipal Create(
        PositionPrincipal position,
        TerminalEnrollment terminal,
        Guid staffingSessionId,
        DateTimeOffset authTime,
        string activationProof)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "Bearer",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, position.Id.ToString());
        identity.SetClaim(Claims.Name, position.DisplayName);
        identity.SetClaim(PositionTokenClaimTypes.PrincipalType, PositionPrincipalTypes.Position);
        identity.SetClaim(PositionTokenClaimTypes.TokenUse, PositionTokenUses.StaffingSession);
        identity.SetClaim(PositionTokenClaimTypes.TerminalId, terminal.Id.ToString());
        identity.SetClaim(PositionTokenClaimTypes.StaffingSessionId, staffingSessionId.ToString());
        identity.SetClaim(PositionTokenClaimTypes.ActivationProof, activationProof);
        identity.SetClaim(PositionTokenClaimTypes.TerminalBinding, terminal.Binding);
        identity.SetClaim(Claims.AuthenticationTime, authTime.ToUnixTimeSeconds());
        identity.SetClaims(Claims.AuthenticationMethodReference,
            [activationProof switch
            {
                ActivationProofMethodIds.PersonalPassword => "pwd",
                ActivationProofMethodIds.PersonalEmailOtp => "otp",
                _ => "webauthn",
            }]);

        var principal = new ClaimsPrincipal(identity);
        // Scopes/resources are applied by the exchange (they depend on the
        // request); every claim goes to the access token only.
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return principal;
    }
}
