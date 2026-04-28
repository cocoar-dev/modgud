namespace TimeToDo.Authentication.Domain;

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

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool HasExceededAttempts => Attempts >= MaxAttempts;
}
