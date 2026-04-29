namespace Cocoar.Auth.Authentication.Sessions;

/// <summary>
/// Resolves the current request's User-Agent into a coarse device descriptor
/// for per-user session tracking. Reads the active HttpContext via Wangkanai's
/// <c>IDetectionService</c> — there is no parameter because the parser binds
/// to the request, not to a hand-supplied string. Always returns a non-null
/// <see cref="DeviceInfo"/>; missing fields collapse to <c>"Unknown"</c>.
/// </summary>
public interface IDeviceInfoService
{
    DeviceInfo Parse();
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
