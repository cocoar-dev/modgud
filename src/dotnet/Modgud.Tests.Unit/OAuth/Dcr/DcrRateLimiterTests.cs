using Modgud.Application.Dcr;

namespace Modgud.Tests.Unit.OAuth.Dcr;

/// <summary>
/// Pins the two-window rate-limit logic. The limiter is in-memory, so
/// these tests share no state across cases (a fresh limiter per test).
/// </summary>
public class DcrRateLimiterTests
{
    private const string RealmId = "system";

    [Fact]
    public void First_request_is_allowed()
    {
        var sut = new DcrRateLimiter();
        Assert.Equal(DcrRateLimitVerdict.Allowed, sut.TryConsume("1.2.3.4", RealmId, 5, 100));
    }

    [Fact]
    public void Per_ip_limit_blocks_further_requests_from_same_ip()
    {
        var sut = new DcrRateLimiter();
        for (var i = 0; i < 5; i++)
            Assert.Equal(DcrRateLimitVerdict.Allowed, sut.TryConsume("1.2.3.4", RealmId, 5, 100));

        Assert.Equal(DcrRateLimitVerdict.PerIpExceeded,
            sut.TryConsume("1.2.3.4", RealmId, 5, 100));
    }

    [Fact]
    public void Per_ip_limit_does_not_affect_different_ip()
    {
        var sut = new DcrRateLimiter();
        for (var i = 0; i < 5; i++)
            sut.TryConsume("1.2.3.4", RealmId, 5, 100);

        Assert.Equal(DcrRateLimitVerdict.Allowed,
            sut.TryConsume("9.9.9.9", RealmId, 5, 100));
    }

    [Fact]
    public void Per_realm_limit_blocks_across_all_ips()
    {
        var sut = new DcrRateLimiter();
        // 4 IPs × 3 hits = 12; per-IP allowance is 5, per-realm is 10. The
        // 11th hit (any IP) trips the realm window because it's reached
        // before any individual IP hits its own limit.
        for (var n = 0; n < 10; n++)
            Assert.Equal(DcrRateLimitVerdict.Allowed,
                sut.TryConsume($"10.0.0.{n}", RealmId, 5, 10));

        Assert.Equal(DcrRateLimitVerdict.PerRealmExceeded,
            sut.TryConsume("10.0.0.99", RealmId, 5, 10));
    }

    [Fact]
    public void Per_realm_limit_isolates_different_realms()
    {
        var sut = new DcrRateLimiter();
        var realm2 = "tenant-acme";

        for (var n = 0; n < 10; n++)
            sut.TryConsume($"10.0.0.{n}", RealmId, 5, 10);

        // Different realm — counter is fresh.
        Assert.Equal(DcrRateLimitVerdict.Allowed,
            sut.TryConsume("10.0.0.99", realm2, 5, 10));
    }

    [Fact]
    public void Per_ip_check_runs_before_per_realm_so_blocked_ip_does_not_consume_realm_budget()
    {
        var sut = new DcrRateLimiter();

        // Hit per-IP limit (5 requests) — the 6th should hit IP exceeded
        // and NOT consume any realm budget.
        for (var i = 0; i < 5; i++)
            sut.TryConsume("1.2.3.4", RealmId, 5, 100);

        Assert.Equal(DcrRateLimitVerdict.PerIpExceeded,
            sut.TryConsume("1.2.3.4", RealmId, 5, 100));

        // A different IP should still get its full per-IP allowance,
        // confirming realm-counter wasn't pre-charged.
        for (var i = 0; i < 5; i++)
            Assert.Equal(DcrRateLimitVerdict.Allowed,
                sut.TryConsume("9.9.9.9", RealmId, 5, 100));
    }
}
