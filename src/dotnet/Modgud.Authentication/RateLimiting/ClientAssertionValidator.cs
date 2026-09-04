using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Modgud.Authentication.RateLimiting;

/// <summary>
/// Validates a <c>private_key_jwt</c> client assertion (RFC 7523 / OIDC Core §9)
/// against the client's registered key set, for the caller context: signature with a
/// registered key, <c>iss</c> = <c>sub</c> = client id, <c>aud</c> naming this server
/// (issuer or token endpoint), unexpired. OpenIddict validates the same assertion
/// again for the token request itself; this check only decides whether the caller
/// counts as an authenticated confidential client for rate-limit purposes.
/// </summary>
internal static class ClientAssertionValidator
{
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private static readonly JsonWebTokenHandler Handler = new();

    public static string? ReadSubject(string assertion)
    {
        if (!Handler.CanReadToken(assertion)) return null;
        try
        {
            var token = Handler.ReadJsonWebToken(assertion);
            return string.Equals(token.Issuer, token.Subject, StringComparison.Ordinal) ? token.Subject : null;
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            return null;
        }
    }

    public static async Task<bool> IsValidAsync(string assertion, string clientId, JsonWebKeySet? keys, HttpRequest request)
    {
        if (keys is null || keys.Keys.Count == 0) return false;

        var origin = $"{request.Scheme}://{request.Host}{request.PathBase}";
        var result = await Handler.ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = clientId,
            ValidateAudience = true,
            // The audience is the issuer identifier or the token endpoint (RFC 7523 §3);
            // both live under this request's origin.
            AudienceValidator = (audiences, _, _) => audiences.Any(a => a.StartsWith(origin, StringComparison.OrdinalIgnoreCase)),
            IssuerSigningKeys = keys.Keys,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        });
        if (!result.IsValid) return false;
        var subject = (result.SecurityToken as JsonWebToken)?.Subject;
        return string.Equals(subject, clientId, StringComparison.Ordinal);
    }
}
