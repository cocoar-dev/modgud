using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Ephemeral document entity for email OTP challenges.
/// Stores the hashed OTP code with attempt tracking and expiration.
/// </summary>
public class EmailOtpChallenge
{
    /// <summary>
    /// The unique identifier (same as UserId for 1:1 mapping).
    /// </summary>
    [JsonInclude]
    public Guid Id { get; set; }

    /// <summary>
    /// The hashed OTP code for verification.
    /// </summary>
    [JsonInclude]
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// Number of verification attempts made.
    /// </summary>
    [JsonInclude]
    public int Attempts { get; set; }

    /// <summary>
    /// Maximum allowed attempts before the challenge is invalidated.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// When the OTP challenge expires.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the OTP challenge was created.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The user's email address (for sending the OTP).
    /// </summary>
    [JsonInclude]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The user's display name (for email personalization).
    /// </summary>
    [JsonInclude]
    public string? UserName { get; set; }

    /// <summary>
    /// Whether this challenge has expired.
    /// </summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>
    /// Whether the maximum attempts have been reached.
    /// </summary>
    public bool HasExceededAttempts => Attempts >= MaxAttempts;

    /// <summary>
    /// Creates a new email OTP challenge.
    /// </summary>
    public static EmailOtpChallenge Create(
        Guid userId,
        string codeHash,
        string email,
        string? userName,
        TimeSpan? expirationTime = null)
    {
        var expiration = expirationTime ?? TimeSpan.FromMinutes(10);

        return new EmailOtpChallenge
        {
            Id = userId,
            CodeHash = codeHash,
            Email = email,
            UserName = userName,
            Attempts = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiration)
        };
    }

    /// <summary>
    /// Increments the attempt counter.
    /// </summary>
    public void IncrementAttempts()
    {
        Attempts++;
    }
}
