namespace Modgud.Authentication.Domain;

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

    /// <summary>AREG-06 — SHA-256 of the normalized address the link was sent to (not the
    /// address itself, so the challenge holds no copy of it). The link confirms only that
    /// address; a challenge without it (issued before this field) is refused.</summary>
    public string? EmailHash { get; set; }

    public static string HashEmail(string normalizedEmail) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalizedEmail)));
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public const int ExpirationHours = 24;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
