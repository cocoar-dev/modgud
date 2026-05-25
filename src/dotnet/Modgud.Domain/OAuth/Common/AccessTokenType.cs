namespace Modgud.Domain.OAuth.Common;

/// <summary>
/// Specifies the type of access token issued to a client.
/// </summary>
public enum AccessTokenType
{
    /// <summary>
    /// Reference token - an opaque identifier returned to the client; payload stays
    /// server-side and is resolved via introspection. Default for max revocability.
    /// </summary>
    Reference = 0,

    /// <summary>
    /// JWT — self-contained token, can be validated without contacting the server.
    /// </summary>
    Jwt = 1
}

/// <summary>
/// Specifies how refresh tokens are handled.
/// </summary>
public enum RefreshTokenUsage
{
    /// <summary>Refresh handle is rotated on each use (default, recommended).</summary>
    OneTimeOnly = 0,

    /// <summary>Refresh handle stays the same across refreshes.</summary>
    ReUse = 1
}

/// <summary>Claim attached to an OAuth client for token issuance.</summary>
public record OAuthClientClaim(string Type, string Value);
