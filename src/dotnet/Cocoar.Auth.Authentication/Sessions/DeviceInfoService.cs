using Wangkanai.Detection.Services;

namespace Cocoar.Auth.Authentication.Sessions;

/// <summary>
/// Wangkanai.Detection-backed <see cref="IDeviceInfoService"/>. Resolves
/// browser / platform / device-type for the active HttpContext via the
/// injected <see cref="IDetectionService"/>. Wangkanai itself is HttpContext-
/// scoped (registered via <c>AddDetection()</c>); this façade adds one
/// translation layer so we don't tie <c>SessionService</c> to a third-party
/// interface and so the mapping into <see cref="DeviceInfo"/> stays
/// unit-testable behind a single seam.
/// </summary>
public class DeviceInfoService(IDetectionService detection) : IDeviceInfoService
{
    public DeviceInfo Parse()
    {
        try
        {
            var browserName = detection.Browser?.Name.ToString();
            var browserVersion = detection.Browser?.Version?.ToString();
            var platformName = detection.Platform?.Name.ToString();
            var platformVersion = detection.Platform?.Version?.ToString();
            var deviceType = detection.Device?.Type.ToString();

            return new DeviceInfo
            {
                Browser = NormaliseUnknown(browserName),
                BrowserVersion = NormaliseVersion(browserVersion),
                OperatingSystem = NormaliseUnknown(platformName),
                OsVersion = NormaliseVersion(platformVersion),
                DeviceType = NormaliseUnknown(deviceType),
            };
        }
        catch
        {
            // Defensive: a malformed/missing User-Agent must never break login.
            return DeviceInfo.Unknown;
        }
    }

    // Wangkanai surfaces "Others" / "Unknown" enum values when it cannot
    // identify a field. Collapse all of them to the same "Unknown" string the
    // legacy UAParser-backed implementation used, so the sessions UI doesn't
    // suddenly render "Others" rows alongside "Unknown" rows.
    private static string? NormaliseUnknown(string? value)
        => string.IsNullOrEmpty(value) || value.Equals("Others", StringComparison.OrdinalIgnoreCase)
            ? "Unknown"
            : value;

    // Wangkanai returns `Version` as a System.Version which stringifies "0.0"
    // for empty values; treat that as "no version known".
    private static string? NormaliseVersion(string? value)
        => string.IsNullOrEmpty(value) || value == "0.0" || value == "0.0.0.0"
            ? null
            : value;
}
