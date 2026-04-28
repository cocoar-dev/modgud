namespace TimeToDo.Authentication.Domain;

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

    public const int ExpirationMinutes = 15;
    public const int RateLimitMinutes = 2;

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
}
