using Cocoar.Auth.Authentication.Sessions;
using Wangkanai.Detection.Models;
using Wangkanai.Detection.Services;

namespace Cocoar.Auth.Tests.Unit.Sessions;

/// <summary>
/// Pins the mapping from Wangkanai.Detection's <see cref="IDetectionService"/>
/// onto our <see cref="DeviceInfo"/> shape. The wrapper itself is the seam —
/// tests drive a hand-rolled <c>IDetectionService</c> rather than trying to
/// construct an <c>HttpContext</c>, which is what Wangkanai itself reads from
/// internally. UA-string-quirk pinning lives in Wangkanai's own test suite.
/// </summary>
public class DeviceInfoServiceTests
{
    public class HappyPath
    {
        [Fact]
        public void Maps_browser_platform_device_into_DeviceInfo()
        {
            var sut = new DeviceInfoService(Detection(
                browser: Browser.Chrome, browserVersion: new Version(120, 0),
                platform: Platform.Windows, platformVersion: new Version(10, 0),
                device: Device.Desktop));

            var info = sut.Parse();

            Assert.Equal("Chrome", info.Browser);
            Assert.Equal("120.0", info.BrowserVersion);
            Assert.Equal("Windows", info.OperatingSystem);
            Assert.Equal("10.0", info.OsVersion);
            Assert.Equal("Desktop", info.DeviceType);
        }

        [Fact]
        public void Mac_safari_is_classified_as_desktop_not_mobile()
        {
            // Pinning the bug-fix this swap delivered: the legacy UAParser-
            // based implementation classified Macintosh user-agents as
            // "Mobile" because of an allow-by-exclusion fallback. Wangkanai's
            // Device service returns Desktop for Mac browsers — and our
            // wrapper passes that through unchanged.
            var sut = new DeviceInfoService(Detection(
                browser: Browser.Safari, browserVersion: new Version(17, 0),
                platform: Platform.Mac, platformVersion: new Version(10, 15, 7),
                device: Device.Desktop));

            var info = sut.Parse();

            Assert.Equal("Safari", info.Browser);
            Assert.Equal("Mac", info.OperatingSystem);
            Assert.Equal("Desktop", info.DeviceType);
        }

        [Fact]
        public void IPhone_safari_is_classified_as_mobile()
        {
            var sut = new DeviceInfoService(Detection(
                browser: Browser.Safari, browserVersion: new Version(17, 0),
                platform: Platform.iOS, platformVersion: new Version(17, 0),
                device: Device.Mobile));

            Assert.Equal("Mobile", sut.Parse().DeviceType);
        }

        [Fact]
        public void IPad_is_classified_as_tablet()
        {
            var sut = new DeviceInfoService(Detection(
                browser: Browser.Safari, browserVersion: new Version(17, 0),
                platform: Platform.iPadOS, platformVersion: new Version(17, 0),
                device: Device.Tablet));

            Assert.Equal("Tablet", sut.Parse().DeviceType);
        }
    }

    public class UnknownAndDefaults
    {
        [Fact]
        public void Wangkanai_Others_collapses_to_Unknown_for_consistent_UI()
        {
            // Wangkanai surfaces "Others" for browsers/platforms it doesn't
            // recognise. The legacy implementation surfaced "Unknown". Pin
            // the collapse so the sessions UI doesn't suddenly grow an
            // "Others" bucket alongside "Unknown".
            var sut = new DeviceInfoService(Detection(
                browser: Browser.Others, browserVersion: null,
                platform: Platform.Others, platformVersion: null,
                device: Device.Unknown));

            var info = sut.Parse();

            Assert.Equal("Unknown", info.Browser);
            Assert.Equal("Unknown", info.OperatingSystem);
            Assert.Equal("Unknown", info.DeviceType);
        }

        [Fact]
        public void Empty_version_collapses_to_null_browser_version()
        {
            // Wangkanai stringifies a missing System.Version as "0.0".
            // We surface that as null so the UI can hide the version
            // chip rather than render "0.0".
            var sut = new DeviceInfoService(Detection(
                browser: Browser.Chrome, browserVersion: new Version(0, 0),
                platform: Platform.Windows, platformVersion: new Version(0, 0, 0, 0),
                device: Device.Desktop));

            var info = sut.Parse();

            Assert.Null(info.BrowserVersion);
            Assert.Null(info.OsVersion);
        }

        [Fact]
        public void Wangkanai_internal_throw_is_swallowed_so_login_never_breaks()
        {
            // Defensive: a malformed User-Agent that breaks Wangkanai's
            // parsing must never block a sign-in. Wrapper falls back to
            // DeviceInfo.Unknown.
            var sut = new DeviceInfoService(new ThrowingDetectionService());

            var info = sut.Parse();

            Assert.Equal(DeviceInfo.Unknown, info);
        }
    }

    public class UnknownMarker
    {
        [Fact]
        public void DeviceInfo_Unknown_advertises_unknown_browser_and_os_and_device()
        {
            Assert.Equal("Unknown", DeviceInfo.Unknown.Browser);
            Assert.Equal("Unknown", DeviceInfo.Unknown.OperatingSystem);
            Assert.Equal("Unknown", DeviceInfo.Unknown.DeviceType);
            Assert.Null(DeviceInfo.Unknown.BrowserVersion);
            Assert.Null(DeviceInfo.Unknown.OsVersion);
        }
    }

    // ── Test seams ──────────────────────────────────────────────────────

    private static IDetectionService Detection(
        Browser browser, Version? browserVersion,
        Platform platform, Version? platformVersion,
        Device device)
        => new FakeDetectionService(
            new FakeBrowserService(browser, browserVersion),
            new FakePlatformService(platform, platformVersion),
            new FakeDeviceService(device));

    private sealed class FakeDetectionService(
        IBrowserService browser,
        IPlatformService platform,
        IDeviceService device) : IDetectionService
    {
        public UserAgent UserAgent => new("");
        public IDeviceService Device => device;
        public IPlatformService Platform => platform;
        public IEngineService Engine => null!;
        public IBrowserService Browser => browser;
        public ICrawlerService Crawler => null!;
    }

    private sealed class FakeBrowserService(Browser name, Version? version) : IBrowserService
    {
        public Browser Name => name;
        public Version Version => version ?? new Version(0, 0);
    }

    private sealed class FakePlatformService(Platform name, Version? version) : IPlatformService
    {
        public Platform Name => name;
        public Version Version => version ?? new Version(0, 0);
        public Processor Processor => Processor.Others;
    }

    private sealed class FakeDeviceService(Device type) : IDeviceService
    {
        public Device Type => type;
    }

    private sealed class ThrowingDetectionService : IDetectionService
    {
        public UserAgent UserAgent => throw new InvalidOperationException("simulated");
        public IDeviceService Device => throw new InvalidOperationException("simulated");
        public IPlatformService Platform => throw new InvalidOperationException("simulated");
        public IEngineService Engine => throw new InvalidOperationException("simulated");
        public IBrowserService Browser => throw new InvalidOperationException("simulated");
        public ICrawlerService Crawler => throw new InvalidOperationException("simulated");
    }
}
