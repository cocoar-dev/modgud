namespace Cocoar.Auth.Authentication.Domain;

/// <summary>
/// Single-use, time-limited token that proves the user controls the email
/// address on file. Used by the account-email verification flow (banner +
/// anonymous self-service page) — distinct from the SelfRegistration
/// pending-doc and the profile change-request flow.
/// Token is stored as SHA256 hash; the plaintext only lives in the email.
/// </summary>
public class EmailVerificationChallenge
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public const int ExpirationHours = 24;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
