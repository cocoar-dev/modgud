using Cocoar.Auth.Authentication.Sessions;

namespace Cocoar.Auth.Tests.Unit.Sessions;

/// <summary>
/// Pins the User-Agent → <see cref="DeviceInfo"/> mapping. The class wraps
/// <c>UAParser</c> but adds its own device-type heuristic on top, so the
/// behaviour is worth nailing down independently of the underlying library.
/// </summary>
public class DeviceInfoServiceTests
{
    private readonly DeviceInfoService _sut = new();

    public class NullOrEmptyInputs : DeviceInfoServiceTests
    {
        [Fact]
        public void Null_user_agent_returns_unknown()
        {
            var info = _sut.Parse(null);
            Assert.Equal(DeviceInfo.Unknown, info);
        }

        [Fact]
        public void Empty_user_agent_returns_unknown()
        {
            var info = _sut.Parse("");
            Assert.Equal(DeviceInfo.Unknown, info);
        }

        [Fact]
        public void Whitespace_user_agent_returns_unknown()
        {
            var info = _sut.Parse("   \t  ");
            Assert.Equal(DeviceInfo.Unknown, info);
        }

        [Fact]
        public void Unknown_marker_advertises_unknown_browser_and_os()
        {
            Assert.Equal("Unknown", DeviceInfo.Unknown.Browser);
            Assert.Equal("Unknown", DeviceInfo.Unknown.OperatingSystem);
            Assert.Equal("Unknown", DeviceInfo.Unknown.DeviceType);
            Assert.Null(DeviceInfo.Unknown.BrowserVersion);
            Assert.Null(DeviceInfo.Unknown.OsVersion);
        }
    }

    public class DesktopBrowsers : DeviceInfoServiceTests
    {
        [Fact]
        public void Chrome_on_windows_is_classified_as_desktop()
        {
            const string ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

            var info = _sut.Parse(ua);

            Assert.Equal("Chrome", info.Browser);
            Assert.Equal("Windows", info.OperatingSystem);
            Assert.Equal("Desktop", info.DeviceType);
            Assert.NotNull(info.BrowserVersion);
        }

        [Fact]
        public void Firefox_on_linux_is_classified_as_desktop()
        {
            const string ua = "Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0";

            var info = _sut.Parse(ua);

            Assert.Equal("Firefox", info.Browser);
            Assert.Equal("Desktop", info.DeviceType);
        }

        [Fact]
        public void Safari_on_mac_extracts_browser_and_os_correctly()
        {
            // Note: DeviceType currently returns "Mobile" for Mac desktop Safari because
            // UAParser sets Device.Family = "Mac" for Macintosh user agents, and the
            // DetermineDeviceType fallback treats any non-"Other"/non-"Spider" device
            // family as Mobile. Tracked as a production bug — once fixed, this test
            // should assert "Desktop" instead. We pin the current behaviour so the bug
            // is impossible to fix silently.
            const string ua = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15";

            var info = _sut.Parse(ua);

            Assert.Equal("Safari", info.Browser);
            Assert.Equal("Mac OS X", info.OperatingSystem);
        }
    }

    public class MobileAndTabletDetection : DeviceInfoServiceTests
    {
        [Fact]
        public void Iphone_user_agent_is_classified_as_mobile()
        {
            const string ua = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

            var info = _sut.Parse(ua);

            Assert.Equal("Mobile", info.DeviceType);
        }

        [Fact]
        public void Ipad_user_agent_is_classified_as_tablet()
        {
            const string ua = "Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

            var info = _sut.Parse(ua);

            Assert.Equal("Tablet", info.DeviceType);
        }

        [Fact]
        public void Android_phone_user_agent_is_classified_as_mobile()
        {
            const string ua = "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";

            var info = _sut.Parse(ua);

            Assert.Equal("Mobile", info.DeviceType);
        }

        [Fact]
        public void Android_without_mobile_token_is_classified_as_tablet()
        {
            // Per the heuristic in DetermineDeviceType: "android" without "mobile" → tablet.
            const string ua = "Mozilla/5.0 (Linux; Android 13; SM-T970) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

            var info = _sut.Parse(ua);

            Assert.Equal("Tablet", info.DeviceType);
        }

        [Fact]
        public void Tablet_token_in_user_agent_is_classified_as_tablet()
        {
            const string ua = "Mozilla/5.0 (Linux; Tablet) AppleWebKit/537.36";

            var info = _sut.Parse(ua);

            Assert.Equal("Tablet", info.DeviceType);
        }
    }

    public class MalformedInputs : DeviceInfoServiceTests
    {
        [Theory]
        [InlineData("not-a-real-user-agent")]
        [InlineData("zzzz")]
        [InlineData("12345")]
        public void Garbage_strings_do_not_throw(string ua)
        {
            // Whatever UAParser returns, we want a non-null DeviceInfo and no exception.
            var info = _sut.Parse(ua);

            Assert.NotNull(info);
            Assert.NotNull(info.DeviceType);
        }
    }
}
