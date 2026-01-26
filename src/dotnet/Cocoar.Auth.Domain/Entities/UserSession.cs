using System.Text.Json.Serialization;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Represents an active user session.
/// This is a Marten document (NOT event-sourced) because sessions are ephemeral state.
/// </summary>
public class UserSession
{
    /// <summary>
    /// The unique identifier for this session.
    /// </summary>
    [JsonInclude]
    public Guid Id { get; set; }

    /// <summary>
    /// The user ID this session belongs to.
    /// </summary>
    [JsonInclude]
    public Guid UserId { get; set; }

    /// <summary>
    /// The session identifier (correlates with the authentication cookie).
    /// </summary>
    [JsonInclude]
    public string? SessionId { get; set; }

    /// <summary>
    /// The client IP address (stored as-is, no external lookups).
    /// </summary>
    [JsonInclude]
    public string? IpAddress { get; set; }

    /// <summary>
    /// The raw User-Agent string.
    /// </summary>
    [JsonInclude]
    public string? UserAgent { get; set; }

    /// <summary>
    /// The browser name (parsed from UserAgent).
    /// </summary>
    [JsonInclude]
    public string? Browser { get; set; }

    /// <summary>
    /// The browser version (parsed from UserAgent).
    /// </summary>
    [JsonInclude]
    public string? BrowserVersion { get; set; }

    /// <summary>
    /// The operating system name (parsed from UserAgent).
    /// </summary>
    [JsonInclude]
    public string? OperatingSystem { get; set; }

    /// <summary>
    /// The operating system version (parsed from UserAgent).
    /// </summary>
    [JsonInclude]
    public string? OsVersion { get; set; }

    /// <summary>
    /// The device type (Desktop, Mobile, Tablet).
    /// </summary>
    [JsonInclude]
    public string? DeviceType { get; set; }

    /// <summary>
    /// When the session was created.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the session was last active.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset LastActiveAt { get; set; }

    /// <summary>
    /// When the session expires.
    /// </summary>
    [JsonInclude]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Creates a new user session.
    /// </summary>
    public static UserSession Create(
        Guid userId,
        string? sessionId,
        string? ipAddress,
        string? userAgent,
        string? browser,
        string? browserVersion,
        string? operatingSystem,
        string? osVersion,
        string? deviceType,
        TimeSpan sessionDuration)
    {
        var now = DateTimeOffset.UtcNow;
        return new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SessionId = sessionId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Browser = browser,
            BrowserVersion = browserVersion,
            OperatingSystem = operatingSystem,
            OsVersion = osVersion,
            DeviceType = deviceType,
            CreatedAt = now,
            LastActiveAt = now,
            ExpiresAt = now.Add(sessionDuration)
        };
    }

    /// <summary>
    /// Updates the last active timestamp.
    /// </summary>
    public void Touch()
    {
        LastActiveAt = DateTimeOffset.UtcNow;
    }
}
