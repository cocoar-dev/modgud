using Modgud.Application.Dcr;
using Modgud.Infrastructure.RateLimiting;

namespace Modgud.Tests.Unit.OAuth.Dcr;

/// <summary>
/// Pins the two-window DCR limit logic on the shared counter store (ADR 0019). Each
/// test gets its own in-memory store, so cases share no state.
/// </summary>
public class DcrRateLimiterTests
{
    private const string RealmId = "system";

    private static StoreBackedDcrRateLimiter Sut() => new(new InMemoryRateLimitStore());

    [Fact]
    public async Task First_request_is_allowed()
    {
        var sut = Sut();
        Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync("1.2.3.4", RealmId, 5, 100));
    }

    [Fact]
    public async Task Per_ip_limit_blocks_further_requests_from_same_ip()
    {
        var sut = Sut();
        for (var i = 0; i < 5; i++)
            Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync("1.2.3.4", RealmId, 5, 100));

        Assert.Equal(DcrRateLimitVerdict.PerIpExceeded, await sut.TryConsumeAsync("1.2.3.4", RealmId, 5, 100));
    }

    [Fact]
    public async Task Per_ip_limit_does_not_affect_different_ip()
    {
        var sut = Sut();
        for (var i = 0; i < 5; i++)
            await sut.TryConsumeAsync("1.2.3.4", RealmId, 5, 100);

        Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync("9.9.9.9", RealmId, 5, 100));
    }

    [Fact]
    public async Task Per_ip_limit_treats_an_ipv6_64_as_one_source()
    {
        // B8-AJ-05 — the same /64 key as every other auth limit; IPv4-mapped is IPv4.
        var sut = Sut();
        Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync("2001:db8:1:2::1", RealmId, 1, 100));
        Assert.Equal(DcrRateLimitVerdict.PerIpExceeded, await sut.TryConsumeAsync("2001:db8:1:2:ffff::2", RealmId, 1, 100));
        Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync("2001:db8:1:3::1", RealmId, 1, 100));

        Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync("::ffff:192.0.2.7", RealmId, 1, 100));
        Assert.Equal(DcrRateLimitVerdict.PerIpExceeded, await sut.TryConsumeAsync("192.0.2.7", RealmId, 1, 100));
    }

    [Fact]
    public async Task Per_realm_limit_blocks_across_all_ips()
    {
        var sut = Sut();
        for (var n = 0; n < 10; n++)
            Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync($"10.0.0.{n}", RealmId, 5, 10));

        Assert.Equal(DcrRateLimitVerdict.PerRealmExceeded, await sut.TryConsumeAsync("10.0.0.99", RealmId, 5, 10));
    }

    [Fact]
    public async Task Per_realm_limit_isolates_different_realms()
    {
        var sut = Sut();
        for (var n = 0; n < 10; n++)
            await sut.TryConsumeAsync($"10.0.0.{n}", RealmId, 5, 10);

        Assert.Equal(DcrRateLimitVerdict.Allowed, await sut.TryConsumeAsync("10.0.0.99", "tenant-acme", 5, 10));
    }

    [Fact]
    public async Task Per_ip_check_runs_before_per_realm_so_blocked_ip_does_not_consume_realm_budget()
    {
        var sut = Sut();
        for (var i = 0; i < 5; i++)
            await sut.TryConsumeAsync("1.2.3.4", RealmId, 5, 100);
        Assert.Equal(DcrRateLimitVerdict.PerIpExceeded, await sut.TryConsumeAsync("1.2.3.4", RealmId, 5, 100));

        // Realm budget 5, already 5 spent by the first ip; a rejected ip hit must not
        // have charged the realm, so a fresh ip still gets its allowance until the
        // realm window fills.
        var sut2 = Sut();
        for (var i = 0; i < 3; i++)
            await sut2.TryConsumeAsync("1.2.3.4", RealmId, 3, 6);
        Assert.Equal(DcrRateLimitVerdict.PerIpExceeded, await sut2.TryConsumeAsync("1.2.3.4", RealmId, 3, 6));
        for (var i = 0; i < 3; i++)
            Assert.Equal(DcrRateLimitVerdict.Allowed, await sut2.TryConsumeAsync("9.9.9.9", RealmId, 3, 6));
    }
}
