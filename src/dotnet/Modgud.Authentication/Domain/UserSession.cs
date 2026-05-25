namespace Modgud.Authentication.Domain;

/// <summary>
/// Represents a per-user device/login session. Stored as a regular Marten
/// document (not event-sourced) — sessions are ephemeral state, recreated
/// on every login. Used by the per-user "active sessions" view and the
/// admin force-logout flow.
/// </summary>
public class UserSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Optional correlation token (e.g. cookie/session id).</summary>
    public string? SessionId { get; set; }

    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    // Parsed device info (from Wangkanai.Detection)
    public string? Browser { get; set; }
    public string? BrowserVersion { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OsVersion { get; set; }
    public string? DeviceType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

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
            ExpiresAt = now.Add(sessionDuration),
        };
    }

    public void Touch() => LastActiveAt = DateTimeOffset.UtcNow;
}
