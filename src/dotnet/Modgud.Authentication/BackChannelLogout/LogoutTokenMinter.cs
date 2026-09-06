using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Modgud.Infrastructure.Realms;

namespace Modgud.Authentication.BackChannelLogout;

/// <summary>
/// ADR 0021 — mints an OpenID Connect Back-Channel Logout 1.0 logout token (§2.4)
/// outside OpenIddict's request-scoped pipeline: RS256 with the realm's active
/// signing key and <c>kid</c>, so a relying party verifies it against the realm JWKS
/// exactly like an ID token. A fresh token is minted per delivery attempt.
/// </summary>
public sealed class LogoutTokenMinter(IRealmKeyStore keys, TimeProvider clock)
{
    /// <summary>Spec §2.4 recommends <c>typ: logout+jwt</c>.</summary>
    public const string TokenType = "logout+jwt";

    public async Task<string> MintAsync(
        string realmSlug,
        string issuer,
        string clientId,
        string subject,
        string? sessionId,
        CancellationToken ct = default)
    {
        var credentials = await keys.GetActiveSigningCredentialsAsync(realmSlug, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            // The events claim carries an empty JSON object for the logout member.
            ["events"] = new Dictionary<string, object>
            {
                [BackChannelLogoutConstants.EventUri] = new Dictionary<string, object>(),
            },
        };
        if (!string.IsNullOrEmpty(sessionId))
            claims["sid"] = sessionId;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = clientId,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, subject)]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(BackChannelLogoutConstants.TokenLifetime),
            SigningCredentials = credentials,
            Claims = claims,
            TokenType = TokenType,
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }
}
