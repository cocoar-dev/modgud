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
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    public static UserSession Create(
        Guid userId,
        string? ipAddress,
        string? userAgent,
        string? browser,
        string? browserVersion,
        string? operatingSystem,
        string? osVersion,
        string? deviceType,
        TimeSpan idleLifetime,
        TimeSpan absoluteLifetime)
    {
        var now = DateTimeOffset.UtcNow;
        return new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Browser = browser,
            BrowserVersion = browserVersion,
            OperatingSystem = operatingSystem,
            OsVersion = osVersion,
            DeviceType = deviceType,
            CreatedAt = now,
            LastActiveAt = now,
            AbsoluteExpiresAt = now.Add(absoluteLifetime),
            ExpiresAt = Min(now.Add(idleLifetime), now.Add(absoluteLifetime)),
        };
    }

    public bool IsActive(DateTimeOffset now) => ExpiresAt > now && AbsoluteExpiresAt > now;

    /// <summary>
    /// Whether a request at <paramref name="now"/> should slide the idle expiry. Normally
    /// at most once per <paramref name="throttle"/>; CS03 — also once half of the current
    /// idle window has passed, so an idle lifetime as short as the throttle can still be
    /// extended by activity. Never when the expiry already sits at the absolute end.
    /// </summary>
    public bool IsDueForTouch(DateTimeOffset now, TimeSpan throttle)
    {
        var elapsed = now - LastActiveAt;
        if (elapsed >= throttle) return true;
        if (ExpiresAt >= AbsoluteExpiresAt) return false;
        return elapsed >= (ExpiresAt - LastActiveAt) / 2;
    }

    public void Touch(DateTimeOffset now, TimeSpan idleLifetime)
    {
        LastActiveAt = now;
        ExpiresAt = Min(now.Add(idleLifetime), AbsoluteExpiresAt);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
