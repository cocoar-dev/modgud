using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Ephemeral Marten document for tracking OIDC external login state.
/// Used to correlate the OAuth authorization request with the callback.
/// One-time use, expires after 10 minutes.
/// </summary>
public class ExternalLoginState
{
    /// <summary>
    /// Unique identifier for this state document.
    /// </summary>
    [JsonInclude]
    public Guid Id { get; set; }

    /// <summary>
    /// The OAuth state parameter (used to correlate request/response).
    /// </summary>
    [JsonInclude]
    public required string State { get; set; }

    /// <summary>
    /// The nonce used for ID token validation.
    /// </summary>
    [JsonInclude]
    public required string Nonce { get; set; }

    /// <summary>
    /// The PKCE code verifier for the authorization code exchange.
    /// </summary>
    [JsonInclude]
    public required string CodeVerifier { get; set; }

    /// <summary>
    /// The name of the login provider (e.g., "google", "microsoft").
    /// </summary>
    [JsonInclude]
    public required string ProviderName { get; set; }

    /// <summary>
    /// The URL to redirect to after successful authentication.
    /// </summary>
    [JsonInclude]
    public required string ReturnUrl { get; set; }

    /// <summary>
    /// If set, this is an account-linking flow for the specified user.
    /// </summary>
    [JsonInclude]
    public Guid? LinkToUserId { get; set; }

    /// <summary>
    /// When this state expires.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When this state was created.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Whether this state has expired.
    /// </summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>
    /// Creates a new external login state.
    /// </summary>
    public static ExternalLoginState Create(
        string state,
        string nonce,
        string codeVerifier,
        string providerName,
        string returnUrl,
        Guid? linkToUserId = null,
        TimeSpan? expirationTime = null)
    {
        var expiration = expirationTime ?? TimeSpan.FromMinutes(10);

        return new ExternalLoginState
        {
            Id = Guid.NewGuid(),
            State = state,
            Nonce = nonce,
            CodeVerifier = codeVerifier,
            ProviderName = providerName,
            ReturnUrl = returnUrl,
            LinkToUserId = linkToUserId,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiration)
        };
    }
}
