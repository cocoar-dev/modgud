namespace Modgud.Authentication.Domain;

/// <summary>
/// Ephemeral Marten document for Magic Link login tokens.
/// One-time use, time-limited. Token is stored as SHA256 hash.
/// </summary>
public class MagicLinkChallenge
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Set when the link is redeemed (Audit #25). The "one-time use" guarantee is a
    /// version-checked Store of this flag rather than a delete: deletes are not
    /// optimistic-concurrency-checked in Marten, so two concurrent redemptions would
    /// both succeed. Marking + storing makes the second concurrent consume throw a
    /// ConcurrencyException, and a non-concurrent replay is rejected by the
    /// IsConsumed gate. Rows are cleaned up by expiry and the per-user request sweep.
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public const int ExpirationMinutes = 15;
    public const int RateLimitMinutes = 2;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsConsumed => ConsumedAt is not null;
}
