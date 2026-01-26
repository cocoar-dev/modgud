namespace Cocoar.Auth.Application.DTOs.Auth;

/// <summary>
/// DTO for session information.
/// </summary>
public record SessionDto
{
    /// <summary>
    /// The session ID.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The client IP address.
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>
    /// The browser name.
    /// </summary>
    public string? Browser { get; init; }

    /// <summary>
    /// The browser version.
    /// </summary>
    public string? BrowserVersion { get; init; }

    /// <summary>
    /// The operating system.
    /// </summary>
    public string? OperatingSystem { get; init; }

    /// <summary>
    /// The operating system version.
    /// </summary>
    public string? OsVersion { get; init; }

    /// <summary>
    /// The device type (Desktop, Mobile, Tablet).
    /// </summary>
    public string? DeviceType { get; init; }

    /// <summary>
    /// When the session was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the session was last active.
    /// </summary>
    public DateTimeOffset LastActiveAt { get; init; }

    /// <summary>
    /// Whether this is the current session.
    /// </summary>
    public bool IsCurrent { get; init; }
}

/// <summary>
/// Response DTO for listing sessions.
/// </summary>
public record SessionListDto
{
    /// <summary>
    /// The list of sessions.
    /// </summary>
    public required List<SessionDto> Sessions { get; init; }
}
