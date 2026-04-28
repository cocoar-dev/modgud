using UAParser;

namespace Cocoar.Auth.Authentication.Sessions;

/// <summary>
/// UAParser-backed <see cref="IDeviceInfoService"/>. The shared <see cref="Parser"/>
/// instance is thread-safe — registered as a singleton.
/// </summary>
public class DeviceInfoService : IDeviceInfoService
{
    private readonly Parser _parser = Parser.GetDefault();

    public DeviceInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return DeviceInfo.Unknown;

        try
        {
            var info = _parser.Parse(userAgent);
            return new DeviceInfo
            {
                Browser = info.UA?.Family,
                BrowserVersion = FormatVersion(info.UA?.Major, info.UA?.Minor, info.UA?.Patch),
                OperatingSystem = info.OS?.Family,
                OsVersion = FormatVersion(info.OS?.Major, info.OS?.Minor, info.OS?.Patch),
                DeviceType = DetermineDeviceType(info.Device, userAgent),
            };
        }
        catch
        {
            return DeviceInfo.Unknown;
        }
    }

    private static string? FormatVersion(string? major, string? minor, string? patch)
    {
        if (string.IsNullOrEmpty(major)) return null;
        var s = major;
        if (!string.IsNullOrEmpty(minor))
        {
            s += $".{minor}";
            if (!string.IsNullOrEmpty(patch)) s += $".{patch}";
        }
        return s;
    }

    private static string DetermineDeviceType(Device? device, string userAgent)
    {
        var ua = userAgent.ToLowerInvariant();
        if (ua.Contains("tablet") || ua.Contains("ipad") || (ua.Contains("android") && !ua.Contains("mobile")))
            return "Tablet";
        if (ua.Contains("mobile") || ua.Contains("iphone") || ua.Contains("android"))
            return "Mobile";

        if (device?.Family is { } family
            && !family.Equals("Other", StringComparison.OrdinalIgnoreCase)
            && !family.Equals("Spider", StringComparison.OrdinalIgnoreCase))
        {
            return "Mobile";
        }

        return "Desktop";
    }
}
