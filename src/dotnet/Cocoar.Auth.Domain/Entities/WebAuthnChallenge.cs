using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Ephemeral document entity for WebAuthn challenges.
/// Used during registration and authentication ceremonies.
/// </summary>
public class WebAuthnChallenge
{
    /// <summary>
    /// Unique identifier for the challenge.
    /// </summary>
    [JsonInclude]
    public Guid Id { get; set; }

    /// <summary>
    /// The challenge bytes (base64 encoded).
    /// </summary>
    [JsonInclude]
    public required string Challenge { get; set; }

    /// <summary>
    /// The user ID this challenge is for.
    /// </summary>
    [JsonInclude]
    public required Guid UserId { get; set; }

    /// <summary>
    /// The type of ceremony: "registration" or "authentication".
    /// </summary>
    [JsonInclude]
    public required string Type { get; set; }

    /// <summary>
    /// The full options JSON for the ceremony (for verification).
    /// </summary>
    [JsonInclude]
    public string? OptionsJson { get; set; }

    /// <summary>
    /// When the challenge expires.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// When the challenge was created.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Whether this challenge has expired.
    /// </summary>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    /// <summary>
    /// Challenge type for registration.
    /// </summary>
    public const string TypeRegistration = "registration";

    /// <summary>
    /// Challenge type for authentication.
    /// </summary>
    public const string TypeAuthentication = "authentication";

    /// <summary>
    /// Creates a new WebAuthn challenge.
    /// </summary>
    public static WebAuthnChallenge Create(
        Guid userId,
        string challenge,
        string type,
        string? optionsJson = null,
        TimeSpan? expirationTime = null)
    {
        var expiration = expirationTime ?? TimeSpan.FromMinutes(5);

        return new WebAuthnChallenge
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Challenge = challenge,
            Type = type,
            OptionsJson = optionsJson,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(expiration)
        };
    }
}
