namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for OIDC protocol operations (Discovery, Authorization URL, Token Exchange, ID Token Validation).
/// </summary>
public interface IOidcProtocolService
{
    /// <summary>
    /// Builds an OIDC authorization URL with PKCE support.
    /// </summary>
    Task<string> BuildAuthorizationUrlAsync(
        OidcProviderConfig config,
        string redirectUri,
        string state,
        string nonce,
        string codeChallenge,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges an authorization code for tokens.
    /// </summary>
    Task<OidcTokenResponse?> ExchangeCodeAsync(
        OidcProviderConfig config,
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates an ID token and extracts user info.
    /// </summary>
    Task<OidcUserInfo?> ValidateIdTokenAsync(
        OidcProviderConfig config,
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Configuration needed for OIDC protocol operations.
/// </summary>
public record OidcProviderConfig(
    string Authority,
    string ClientId,
    string ClientSecret,
    string? Scopes);

/// <summary>
/// Response from the OIDC token endpoint.
/// </summary>
public record OidcTokenResponse(
    string IdToken,
    string? AccessToken,
    string? RefreshToken);

/// <summary>
/// User info extracted from the ID token.
/// </summary>
public record OidcUserInfo(
    string Subject,
    string? Email,
    bool EmailVerified,
    string? Name,
    string? GivenName,
    string? FamilyName,
    string? PreferredUsername);
