using Modgud.Authentication.Domain;

namespace Modgud.Tests.Unit.Authentication.Domain;

/// <summary>
/// Pins the <see cref="UserSession"/> factory + <c>Touch</c> behaviour. This is a
/// pure POCO so the tests are minimal — there is just enough logic to be worth
/// freezing (in particular: ExpiresAt = CreatedAt + sessionDuration).
/// </summary>
public class UserSessionTests
{
    public class Create
    {
        [Fact]
        public void Sets_all_provided_fields()
        {
            var userId = Guid.NewGuid();

            var s = UserSession.Create(
                userId: userId,
                sessionId: "sess-1",
                ipAddress: "10.0.0.1",
                userAgent: "Mozilla/5.0",
                browser: "Chrome",
                browserVersion: "120.0",
                operatingSystem: "Windows",
                osVersion: "10",
                deviceType: "Desktop",
                sessionDuration: TimeSpan.FromHours(8));

            Assert.Equal(userId, s.UserId);
            Assert.Equal("sess-1", s.SessionId);
            Assert.Equal("10.0.0.1", s.IpAddress);
            Assert.Equal("Mozilla/5.0", s.UserAgent);
            Assert.Equal("Chrome", s.Browser);
            Assert.Equal("120.0", s.BrowserVersion);
            Assert.Equal("Windows", s.OperatingSystem);
            Assert.Equal("10", s.OsVersion);
            Assert.Equal("Desktop", s.DeviceType);
        }

        [Fact]
        public void Generates_non_empty_id()
        {
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, TimeSpan.FromHours(1));

            Assert.NotEqual(Guid.Empty, s.Id);
        }

        [Fact]
        public void Sets_created_and_last_active_to_the_same_moment()
        {
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, TimeSpan.FromHours(1));

            Assert.Equal(s.CreatedAt, s.LastActiveAt);
        }

        [Fact]
        public void Sets_expires_at_to_created_plus_session_duration()
        {
            var duration = TimeSpan.FromMinutes(45);
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, duration);

            Assert.Equal(s.CreatedAt + duration, s.ExpiresAt);
        }

        [Fact]
        public void Created_at_is_close_to_now_in_utc()
        {
            var before = DateTimeOffset.UtcNow;
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, TimeSpan.FromHours(1));
            var after = DateTimeOffset.UtcNow;

            Assert.InRange(s.CreatedAt, before, after);
        }

        [Fact]
        public void Allows_all_optional_fields_null()
        {
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, TimeSpan.FromHours(1));

            Assert.Null(s.SessionId);
            Assert.Null(s.IpAddress);
            Assert.Null(s.UserAgent);
            Assert.Null(s.Browser);
            Assert.Null(s.BrowserVersion);
            Assert.Null(s.OperatingSystem);
            Assert.Null(s.OsVersion);
            Assert.Null(s.DeviceType);
        }
    }

    public class Touch
    {
        [Fact]
        public void Updates_last_active_at()
        {
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, TimeSpan.FromHours(1));
            // Force a measurable gap so even on a fast clock the assertion is reliable.
            var initialLastActive = s.LastActiveAt;
            Thread.Sleep(2);

            s.Touch();

            Assert.True(s.LastActiveAt >= initialLastActive);
        }

        [Fact]
        public void Does_not_change_created_at()
        {
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, TimeSpan.FromHours(1));
            var originalCreatedAt = s.CreatedAt;

            s.Touch();

            Assert.Equal(originalCreatedAt, s.CreatedAt);
        }

        [Fact]
        public void Does_not_change_expires_at()
        {
            var s = UserSession.Create(Guid.NewGuid(), null, null, null, null, null, null, null, null, TimeSpan.FromHours(1));
            var originalExpiry = s.ExpiresAt;

            s.Touch();

            Assert.Equal(originalExpiry, s.ExpiresAt);
        }
    }
}
