namespace Modgud.Authentication.Domain;

/// <summary>
/// Ephemeral Marten document for Email OTP challenges.
/// Stored with Id = UserId (1:1 mapping). Overwritten on each new request.
/// </summary>
public class EmailOtpChallenge
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = "";
    public int Attempts { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Email { get; set; } = "";

    public const int MaxAttempts = 3;
    public const int ExpirationMinutes = 10;
    public const int RateLimitMinutes = 2;

    /// <summary>
    /// Set when the code was successfully redeemed. Consuming is a
    /// version-checked <c>Store</c> of this marker rather than a Delete —
    /// Marten does NOT enforce optimistic concurrency on deletes, so two
    /// concurrent redemptions of the same code would both delete-and-proceed.
    /// A later replay is rejected by the <see cref="IsConsumed"/> gate. The row
    /// is reaped by expiry / the next issue for this user.
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsConsumed => ConsumedAt is not null;
    public bool HasExceededAttempts => Attempts >= MaxAttempts;
}
