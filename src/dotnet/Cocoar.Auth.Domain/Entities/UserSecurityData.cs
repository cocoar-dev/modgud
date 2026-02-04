using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Document entity for security-sensitive user data.
/// This data is NOT event-sourced to avoid storing sensitive information in the event history.
/// Uses the same ID as the UserAggregate for correlation.
/// </summary>
public class UserSecurityData
{
    /// <summary>
    /// The unique identifier for this user (same as UserAggregate.Id).
    /// </summary>
    [JsonInclude]
    public Guid Id { get; set; }

    /// <summary>
    /// The hashed password.
    /// </summary>
    [JsonInclude]
    public string? PasswordHash { get; set; }

    /// <summary>
    /// A random value that changes when security-sensitive data changes.
    /// Used for invalidating sessions/tokens.
    /// </summary>
    [JsonInclude]
    public string SecurityStamp { get; set; } = string.Empty;

    /// <summary>
    /// A random value that changes when the document is persisted.
    /// Used for optimistic concurrency.
    /// </summary>
    [JsonInclude]
    public string ConcurrencyStamp { get; set; } = string.Empty;

    /// <summary>
    /// The authenticator key for TOTP-based two-factor authentication.
    /// </summary>
    [JsonInclude]
    public string? AuthenticatorKey { get; set; }

    /// <summary>
    /// The recovery codes for two-factor authentication.
    /// </summary>
    [JsonInclude]
    public List<string> RecoveryCodes { get; set; } = [];

    /// <summary>
    /// User login providers (external logins).
    /// </summary>
    [JsonInclude]
    public List<UserLogin> Logins { get; set; } = [];

    /// <summary>
    /// User tokens (password reset, email confirmation, etc.).
    /// </summary>
    [JsonInclude]
    public List<UserToken> Tokens { get; set; } = [];

    /// <summary>
    /// WebAuthn credentials for passwordless and 2FA authentication.
    /// </summary>
    [JsonInclude]
    public List<WebAuthnCredential> WebAuthnCredentials { get; set; } = [];

    /// <summary>
    /// Creates a new UserSecurityData with fresh stamps.
    /// </summary>
    public static UserSecurityData Create(Guid userId)
    {
        return new UserSecurityData
        {
            Id = userId,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        };
    }

    /// <summary>
    /// Rotates the security stamp (invalidates all sessions/tokens).
    /// </summary>
    public void RotateSecurityStamp()
    {
        SecurityStamp = Guid.NewGuid().ToString();
        ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Updates the concurrency stamp.
    /// </summary>
    public void UpdateConcurrencyStamp()
    {
        ConcurrencyStamp = Guid.NewGuid().ToString();
    }
}
