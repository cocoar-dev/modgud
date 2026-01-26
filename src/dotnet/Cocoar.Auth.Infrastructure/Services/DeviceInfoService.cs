using Cocoar.Auth.Application.Interfaces;
using UAParser;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Service for parsing device information from User-Agent strings using UAParser.
/// </summary>
public class DeviceInfoService : IDeviceInfoService
{
    private readonly Parser _parser;

    public DeviceInfoService()
    {
        _parser = Parser.GetDefault();
    }

    public DeviceInfo Parse(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return DeviceInfo.Unknown;
        }

        try
        {
            var clientInfo = _parser.Parse(userAgent);

            return new DeviceInfo
            {
                Browser = clientInfo.UA?.Family,
                BrowserVersion = FormatVersion(clientInfo.UA?.Major, clientInfo.UA?.Minor, clientInfo.UA?.Patch),
                OperatingSystem = clientInfo.OS?.Family,
                OsVersion = FormatVersion(clientInfo.OS?.Major, clientInfo.OS?.Minor, clientInfo.OS?.Patch),
                DeviceType = DetermineDeviceType(clientInfo.Device, userAgent)
            };
        }
        catch
        {
            return DeviceInfo.Unknown;
        }
    }

    private static string? FormatVersion(string? major, string? minor, string? patch)
    {
        if (string.IsNullOrEmpty(major))
            return null;

        var version = major;
        if (!string.IsNullOrEmpty(minor))
        {
            version += $".{minor}";
            if (!string.IsNullOrEmpty(patch))
            {
                version += $".{patch}";
            }
        }
        return version;
    }

    private static string DetermineDeviceType(Device? device, string userAgent)
    {
        // Check if it's a known mobile device
        if (device?.Family != null &&
            !device.Family.Equals("Other", StringComparison.OrdinalIgnoreCase) &&
            !device.Family.Equals("Spider", StringComparison.OrdinalIgnoreCase))
        {
            // Check common tablet patterns
            var lowerAgent = userAgent.ToLowerInvariant();
            if (lowerAgent.Contains("tablet") ||
                lowerAgent.Contains("ipad") ||
                (lowerAgent.Contains("android") && !lowerAgent.Contains("mobile")))
            {
                return "Tablet";
            }

            // Check common mobile patterns
            if (lowerAgent.Contains("mobile") ||
                lowerAgent.Contains("iphone") ||
                lowerAgent.Contains("android"))
            {
                return "Mobile";
            }
        }

        // Simple heuristic for mobile detection from user agent
        var ua = userAgent.ToLowerInvariant();
        if (ua.Contains("mobile") || ua.Contains("iphone") || ua.Contains("android"))
        {
            if (ua.Contains("tablet") || ua.Contains("ipad"))
                return "Tablet";
            return "Mobile";
        }

        return "Desktop";
    }
}
