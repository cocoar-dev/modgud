using System.Security.Claims;
using Modgud.Authorization.Principals;
using Modgud.Domain.FunctionTerminals;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.FunctionTerminals;

/// <summary>
/// Builds the claims principal for STAFFING-SESSION tokens (MG-FT-05, plan
/// §7.3): the business token a staffed terminal works with. The subject is the
/// FUNCTION; the activating person's identity is deliberately absent (security
/// metadata lives on the <see cref="StaffingSession"/> document only —
/// consuming systems must never see who tapped). <c>amr=webauthn</c> and
/// <c>auth_time</c> document HOW and WHEN the shift was opened.
/// </summary>
public static class FunctionStaffingPrincipal
{
    public static ClaimsPrincipal Create(
        FunctionPrincipal function,
        TerminalEnrollment terminal,
        Guid staffingSessionId,
        DateTimeOffset authTime)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "Bearer",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, function.Id.ToString());
        identity.SetClaim(Claims.Name, function.DisplayName);
        identity.SetClaim(FunctionTokenClaimTypes.PrincipalType, FunctionPrincipalTypes.Function);
        identity.SetClaim(FunctionTokenClaimTypes.TokenUse, FunctionTokenUses.StaffingSession);
        identity.SetClaim(FunctionTokenClaimTypes.TerminalId, terminal.Id.ToString());
        identity.SetClaim(FunctionTokenClaimTypes.StaffingSessionId, staffingSessionId.ToString());
        identity.SetClaim(Claims.AuthenticationTime, authTime.ToUnixTimeSeconds());
        identity.SetClaims(Claims.AuthenticationMethodReference, ["webauthn"]);

        var principal = new ClaimsPrincipal(identity);
        // Scopes/resources are applied by the exchange (they depend on the
        // request); every claim goes to the access token only.
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return principal;
    }
}
