namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for parsing device information from User-Agent strings.
/// </summary>
public interface IDeviceInfoService
{
    /// <summary>
    /// Parses the User-Agent string and returns device information.
    /// </summary>
    DeviceInfo Parse(string? userAgent);
}

/// <summary>
/// Information about the client device parsed from User-Agent.
/// </summary>
public record DeviceInfo
{
    /// <summary>
    /// The browser name.
    /// </summary>
    public string? Browser { get; init; }

    /// <summary>
    /// The browser version.
    /// </summary>
    public string? BrowserVersion { get; init; }

    /// <summary>
    /// The operating system name.
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
    /// Creates an empty DeviceInfo for unknown user agents.
    /// </summary>
    public static DeviceInfo Unknown => new()
    {
        Browser = "Unknown",
        OperatingSystem = "Unknown",
        DeviceType = "Unknown"
    };
}
