using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Modgud.Authorization.Principals;
using Modgud.Domain.FunctionTerminals;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.FunctionTerminals;

/// <summary>
/// Builds the claims principal for terminal-ENROLLMENT tokens (MG-FT-04, plan
/// §11.5). Deliberately NOT <c>CreateClaimsPrincipalAsync</c>: the subject is
/// the FUNCTION, not a person — no user claims, no security stamp, no group
/// bake. The token authorizes exactly one thing: driving the terminal-control
/// surface (begin a staffing ceremony, MG-FT-05). It carries no business
/// audience and no business scopes (MG-FT-04 done criterion).
/// </summary>
public static class TerminalEnrollmentPrincipal
{
    public static ClaimsPrincipal Create(FunctionPrincipal function, TerminalEnrollment terminal)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "Bearer",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, function.Id.ToString());
        identity.SetClaim(Claims.Name, function.DisplayName);
        identity.SetClaim(FunctionTokenClaimTypes.PrincipalType, FunctionPrincipalTypes.Function);
        identity.SetClaim(FunctionTokenClaimTypes.TokenUse, FunctionTokenUses.TerminalEnrollment);
        identity.SetClaim(FunctionTokenClaimTypes.TerminalId, terminal.Id.ToString());

        var principal = new ClaimsPrincipal(identity);
        // offline_access keeps the enrollment chain refreshable (the terminal
        // must survive token expiry between shifts); the terminal-control marker
        // scope + audience are the ONLY surface this token reaches.
        principal.SetScopes(Scopes.OfflineAccess, FunctionTerminalControl.Scope);
        principal.SetResources(FunctionTerminalControl.Audience);
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return principal;
    }

    /// <summary>Human-checkable short fingerprint of a DPoP key thumbprint for
    /// the consent screen (plan §11.4, e.g. <c>2B7A-91D4</c>) — first 8 hex
    /// chars of SHA-256(jkt), grouped for readability.</summary>
    public static string Fingerprint(string jkt)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jkt)));
        return $"{hex[..4]}-{hex[4..8]}";
    }
}
