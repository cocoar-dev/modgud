using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Modgud.Authorization.Principals;
using Modgud.Domain.PositionTerminals;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Modgud.Api.Features.Auth.PositionTerminals;

/// <summary>
/// Builds the claims principal for terminal-ENROLLMENT tokens (MG-FT-04, plan
/// §11.5). Deliberately NOT <c>CreateClaimsPrincipalAsync</c>: V2 makes the
/// terminal the subject, while refresh chains issued by V1 retain the original
/// position subject during the compatibility window. Neither form represents
/// a person, so neither carries user claims, security stamps, or group grants.
/// The token authorizes only the terminal-control surface and carries no
/// business audience or business scopes (MG-FT-04 done criterion).
/// </summary>
public static class TerminalEnrollmentPrincipal
{
    /// <summary>Control-plane V2: the terminal is the subject. Business
    /// position selection is deferred to the staffing ceremony.</summary>
    public static ClaimsPrincipal CreateV2(TerminalEnrollment terminal)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "Bearer",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, terminal.Id.ToString());
        identity.SetClaim(Claims.Name, terminal.DisplayName);
        identity.SetClaim(PositionTokenClaimTypes.PrincipalType, PositionPrincipalTypes.Terminal);
        identity.SetClaim(PositionTokenClaimTypes.TokenUse, PositionTokenUses.TerminalEnrollment);
        identity.SetClaim(PositionTokenClaimTypes.TerminalId, terminal.Id.ToString());
        identity.SetClaim(PositionTokenClaimTypes.TerminalBinding, terminal.Binding);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(Scopes.OfflineAccess, PositionTerminalControl.Scope);
        principal.SetResources(PositionTerminalControl.Audience);
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return principal;
    }

    /// <summary>Legacy Control-plane V1; retained for refresh chains issued
    /// before F4 while the slot still has exactly its original position.</summary>
    public static ClaimsPrincipal Create(PositionPrincipal position, TerminalEnrollment terminal)
    {
        var identity = new ClaimsIdentity(
            authenticationType: "Bearer",
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, position.Id.ToString());
        identity.SetClaim(Claims.Name, position.DisplayName);
        identity.SetClaim(PositionTokenClaimTypes.PrincipalType, PositionPrincipalTypes.Position);
        identity.SetClaim(PositionTokenClaimTypes.TokenUse, PositionTokenUses.TerminalEnrollment);
        identity.SetClaim(PositionTokenClaimTypes.TerminalId, terminal.Id.ToString());
        identity.SetClaim(PositionTokenClaimTypes.TerminalBinding, terminal.Binding);

        var principal = new ClaimsPrincipal(identity);
        // offline_access keeps the enrollment chain refreshable (the terminal
        // must survive token expiry between shifts); the terminal-control marker
        // scope + audience are the ONLY surface this token reaches.
        principal.SetScopes(Scopes.OfflineAccess, PositionTerminalControl.Scope);
        principal.SetResources(PositionTerminalControl.Audience);
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
