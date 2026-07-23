using Modgud.Authentication.Domain;

namespace Modgud.Tests.Unit.Authentication.Domain;

public class UserSessionTests
{
    private static UserSession CreateSession(
        TimeSpan? idleLifetime = null,
        TimeSpan? absoluteLifetime = null) =>
        UserSession.Create(
            Guid.NewGuid(),
            "10.0.0.1",
            "Mozilla/5.0",
            "Chrome",
            "120.0",
            "Windows",
            "10",
            "Desktop",
            idleLifetime ?? TimeSpan.FromHours(1),
            absoluteLifetime ?? TimeSpan.FromHours(8));

    [Fact]
    public void Create_sets_device_data_and_a_stable_id()
    {
        var session = CreateSession();

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal("10.0.0.1", session.IpAddress);
        Assert.Equal("Mozilla/5.0", session.UserAgent);
        Assert.Equal("Chrome", session.Browser);
        Assert.Equal("120.0", session.BrowserVersion);
        Assert.Equal("Windows", session.OperatingSystem);
        Assert.Equal("10", session.OsVersion);
        Assert.Equal("Desktop", session.DeviceType);
        Assert.Equal(session.CreatedAt, session.LastActiveAt);
    }

    [Fact]
    public void Create_caps_idle_expiry_at_absolute_expiry()
    {
        var session = CreateSession(
            idleLifetime: TimeSpan.FromDays(10),
            absoluteLifetime: TimeSpan.FromDays(2));

        Assert.Equal(session.AbsoluteExpiresAt, session.ExpiresAt);
        Assert.Equal(session.CreatedAt.AddDays(2), session.AbsoluteExpiresAt);
    }

    [Fact]
    public void Touch_slides_idle_expiry_without_moving_absolute_expiry()
    {
        var session = CreateSession(
            idleLifetime: TimeSpan.FromHours(1),
            absoluteLifetime: TimeSpan.FromHours(8));
        var absoluteExpiry = session.AbsoluteExpiresAt;
        var now = session.CreatedAt.AddMinutes(30);

        session.Touch(now, TimeSpan.FromHours(1));

        Assert.Equal(now, session.LastActiveAt);
        Assert.Equal(now.AddHours(1), session.ExpiresAt);
        Assert.Equal(absoluteExpiry, session.AbsoluteExpiresAt);
    }

    [Fact]
    public void Touch_never_extends_past_absolute_expiry()
    {
        var session = CreateSession(
            idleLifetime: TimeSpan.FromHours(1),
            absoluteLifetime: TimeSpan.FromHours(2));

        session.Touch(session.CreatedAt.AddMinutes(90), TimeSpan.FromHours(1));

        Assert.Equal(session.AbsoluteExpiresAt, session.ExpiresAt);
    }

    [Fact]
    public void IsActive_requires_both_idle_and_absolute_windows()
    {
        var session = CreateSession();

        Assert.True(session.IsActive(session.CreatedAt.AddMinutes(30)));
        Assert.False(session.IsActive(session.ExpiresAt));
        Assert.False(session.IsActive(session.AbsoluteExpiresAt));
    }
}
