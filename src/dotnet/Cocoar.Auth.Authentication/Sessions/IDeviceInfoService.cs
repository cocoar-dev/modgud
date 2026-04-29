namespace Cocoar.Auth.Authentication.Sessions;

/// <summary>
/// Parses the User-Agent header into a coarse device descriptor for
/// per-user session tracking. Returns <see cref="DeviceInfo.Unknown"/>
/// for null/blank/unparseable input — never throws.
/// </summary>
public interface IDeviceInfoService
{
    DeviceInfo Parse(string? userAgent);
}

public sealed record DeviceInfo
{
    public string? Browser { get; init; }
    public string? BrowserVersion { get; init; }
    public string? OperatingSystem { get; init; }
    public string? OsVersion { get; init; }
    public string? DeviceType { get; init; }

    public static DeviceInfo Unknown => new()
    {
        Browser = "Unknown",
        OperatingSystem = "Unknown",
        DeviceType = "Unknown",
    };
}
