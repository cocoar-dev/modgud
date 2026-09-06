using Modgud.Authentication.Devices;
using Modgud.Authentication.RateLimiting;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.RateLimiting;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>ADR 0020 — the two failure buckets, the spray signal, the unlock guard,
/// log-only, and that failures (not attempts) are what count.</summary>
public class LoginThrottleCoreTests
{
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly RateLimitScope Scope = new("acme");
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Device = Guid.NewGuid();

    private static (LoginThrottleCore Sut, TestClock Clock, InMemoryRateLimitStore Store) Build()
    {
        var clock = new TestClock(T0);
        var store = new InMemoryRateLimitStore();
        return (new LoginThrottleCore(store, clock), clock, store);
    }

    private static AuthRateLimitSettings Login(PolicyLimits limits, RateLimitEnforcementMode? mode = null) => new()
    {
        Policies = new Dictionary<string, PolicyLimits> { ["login"] = limits },
        Mode = mode,
    };

    [Fact]
    public void Defaults_two_buckets_and_a_signal_only_source()
    {
        var login = AuthRateLimitDefaults.ForPolicy(AuthRateLimitPolicy.Login);
        Assert.Equal(10, login.Device!.PermitLimit);
        Assert.Equal(5, login.Target!.PermitLimit);
        Assert.Equal(200, login.Source!.PermitLimit);
        Assert.Null(login.Client);
        Assert.Null(login.App);
        Assert.True(AuthRateLimitDefaults.IsSignalOnly(AuthRateLimitPolicy.Login, RateLimitDimension.Source));
        Assert.False(AuthRateLimitDefaults.IsSignalOnly(AuthRateLimitPolicy.Login, RateLimitDimension.Target));
        Assert.False(AuthRateLimitDefaults.IsSignalOnly(AuthRateLimitPolicy.NativeOtp, RateLimitDimension.Source));
        Assert.Equal("login", AuthRateLimitDefaults.PolicyName(AuthRateLimitPolicy.Login));
    }

    [Fact]
    public async Task Checking_never_charges_only_failures_do()
    {
        var (sut, _, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(2, 15) });

        for (var i = 0; i < 10; i++)
            Assert.True((await sut.CheckAsync(Scope, User, null, false, settings)).Allowed);

        var d = await sut.CheckAsync(Scope, User, null, false, settings);
        await sut.RecordFailureAsync(Scope, User, d, "203.0.113.1", false, settings);
        await sut.RecordFailureAsync(Scope, User, d, "203.0.113.1", false, settings);

        var refused = await sut.CheckAsync(Scope, User, null, false, settings);
        Assert.False(refused.Allowed);
        Assert.Equal(LoginBucket.Untrusted, refused.Bucket);
        Assert.True(refused.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task An_untrusted_attacker_never_locks_the_users_own_device()
    {
        var (sut, _, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(3, 15), Device = RateLimitRule.Fixed(10, 15) });

        for (var i = 0; i < 3; i++)
        {
            var d = await sut.CheckAsync(Scope, User, null, false, settings);
            await sut.RecordFailureAsync(Scope, User, d, $"198.51.100.{i}", false, settings);
        }
        Assert.False((await sut.CheckAsync(Scope, User, null, false, settings)).Allowed);

        // The owner's trusted browser is in its own bucket: untouched.
        var own = await sut.CheckAsync(Scope, User, Device, trusted: true, settings);
        Assert.True(own.Allowed);
        Assert.Equal(LoginBucket.Device, own.Bucket);

        // A stranger presenting a cookie that is NOT trusted for this user is untrusted.
        var foreign = await sut.CheckAsync(Scope, User, Guid.NewGuid(), trusted: false, settings);
        Assert.False(foreign.Allowed);
        Assert.Equal(LoginBucket.Untrusted, foreign.Bucket);
    }

    [Fact]
    public async Task A_tripped_device_bucket_affects_that_device_only()
    {
        var (sut, _, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(5, 15), Device = RateLimitRule.Fixed(2, 15) });
        var laptop = Guid.NewGuid();
        var phone = Guid.NewGuid();

        for (var i = 0; i < 2; i++)
        {
            var d = await sut.CheckAsync(Scope, User, laptop, true, settings);
            await sut.RecordFailureAsync(Scope, User, d, "203.0.113.1", false, settings);
        }
        Assert.False((await sut.CheckAsync(Scope, User, laptop, true, settings)).Allowed);
        Assert.True((await sut.CheckAsync(Scope, User, phone, true, settings)).Allowed);
        Assert.True((await sut.CheckAsync(Scope, User, null, false, settings)).Allowed);
    }

    [Fact]
    public async Task Unlock_mail_is_due_exactly_once_per_window()
    {
        var (sut, clock, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(2, 15) });

        var outcomes = new List<LoginFailureOutcome>();
        for (var i = 0; i < 5; i++)
        {
            var d = await sut.CheckAsync(Scope, User, null, false, settings);
            outcomes.Add(await sut.RecordFailureAsync(Scope, User, d, "203.0.113.1", false, settings));
        }
        // The second failure fills the 2-failure bucket: from then on it is tripped.
        Assert.Equal([false, true, true, true, true], outcomes.Select(o => o.BucketTripped));
        Assert.Equal(1, outcomes.Count(o => o.UnlockDue));

        clock.Now = T0.AddMinutes(16);
        var later = await sut.CheckAsync(Scope, User, null, false, settings);
        Assert.True(later.Allowed);
        for (var i = 0; i < 3; i++)
            outcomes.Add(await sut.RecordFailureAsync(Scope, User, later, "203.0.113.1", false, settings));
        Assert.Equal(2, outcomes.Count(o => o.UnlockDue));
    }

    [Fact]
    public async Task Spray_signal_fires_once_per_window_per_source_and_never_refuses()
    {
        var (sut, _, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(100, 15), Source = RateLimitRule.Fixed(3, 15) });

        var detected = 0;
        for (var i = 0; i < 8; i++)
        {
            var victim = Guid.NewGuid(); // one wrong password per account: spray
            var d = await sut.CheckAsync(Scope, victim, null, false, settings);
            Assert.True(d.Allowed);
            var o = await sut.RecordFailureAsync(Scope, victim, d, "203.0.113.7", false, settings);
            if (o.SprayDetected) detected++;
        }
        Assert.Equal(1, detected);

        // Every one of those accounts is still allowed: the source counter blocks nothing.
        Assert.True((await sut.CheckAsync(Scope, Guid.NewGuid(), null, false, settings)).Allowed);
    }

    [Fact]
    public async Task Allowlisted_sources_and_trusted_devices_do_not_feed_the_spray_signal()
    {
        var (sut, _, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(100, 15), Device = RateLimitRule.Fixed(100, 15), Source = RateLimitRule.Fixed(1, 15) });

        var a = await sut.CheckAsync(Scope, Guid.NewGuid(), null, false, settings);
        Assert.False((await sut.RecordFailureAsync(Scope, Guid.NewGuid(), a, "10.0.0.1", sourceAllowlisted: true, settings)).SprayDetected);
        Assert.False((await sut.RecordFailureAsync(Scope, Guid.NewGuid(), a, "10.0.0.1", sourceAllowlisted: true, settings)).SprayDetected);

        var trusted = await sut.CheckAsync(Scope, User, Device, true, settings);
        Assert.False((await sut.RecordFailureAsync(Scope, User, trusted, "10.0.0.2", false, settings)).SprayDetected);
        Assert.False((await sut.RecordFailureAsync(Scope, User, trusted, "10.0.0.2", false, settings)).SprayDetected);
    }

    [Fact]
    public async Task Log_only_reports_exhaustion_but_allows()
    {
        var (sut, _, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(1, 15) }, RateLimitEnforcementMode.LogOnly);

        var d = await sut.CheckAsync(Scope, User, null, false, settings);
        await sut.RecordFailureAsync(Scope, User, d, "203.0.113.1", false, settings);

        var again = await sut.CheckAsync(Scope, User, null, false, settings);
        Assert.True(again.Allowed);
        Assert.True(again.Exhausted);
        Assert.True(again.LogOnly);
    }

    [Fact]
    public async Task A_disabled_bucket_never_refuses()
    {
        var (sut, _, _) = Build();
        var settings = Login(new PolicyLimits { Target = RateLimitRule.Fixed(1, 15) with { Enabled = false } });
        var d = await sut.CheckAsync(Scope, User, null, false, settings);
        for (var i = 0; i < 5; i++) await sut.RecordFailureAsync(Scope, User, d, "203.0.113.1", false, settings);
        Assert.True((await sut.CheckAsync(Scope, User, null, false, settings)).Allowed);
    }

    [Fact]
    public async Task Peek_matches_hit_for_fixed_windows_and_buckets()
    {
        var store = new InMemoryRateLimitStore();
        var fixedRule = RateLimitRule.Fixed(2, 15);
        Assert.True((await store.PeekAsync(Scope, "k", fixedRule, T0)).Allowed);
        await store.HitAsync(Scope, "k", fixedRule, T0);
        Assert.True((await store.PeekAsync(Scope, "k", fixedRule, T0)).Allowed);
        await store.HitAsync(Scope, "k", fixedRule, T0);
        Assert.False((await store.PeekAsync(Scope, "k", fixedRule, T0)).Allowed);
        Assert.True((await store.PeekAsync(Scope, "k", fixedRule, T0.AddMinutes(15))).Allowed);

        var bucket = RateLimitRule.Bucket(60, 1, 2);
        await store.HitAsync(Scope, "b", bucket, T0);
        await store.HitAsync(Scope, "b", bucket, T0);
        Assert.False((await store.PeekAsync(Scope, "b", bucket, T0)).Allowed);
        Assert.True((await store.PeekAsync(Scope, "b", bucket, T0.AddSeconds(2))).Allowed);
    }

    [Theory]
    [InlineData("auth.acme.test", "acme.test", "acme.test")]
    [InlineData("acme.test", "acme.test", "acme.test")]
    [InlineData("login.other.test", "acme.test", null)]
    [InlineData("acme.test", null, null)]
    public void Device_cookie_domain_widens_only_under_the_primary_domain(string host, string? primary, string? expected)
    {
        Assert.Equal(expected, DeviceTrust.CookieDomainFor(host, primary));
    }

    [Fact]
    public void Trusted_device_keeps_the_last_ten_users()
    {
        var device = new TrustedDevice { Id = Guid.NewGuid(), CreatedAt = T0, LastSeenAt = T0 };
        var users = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();
        foreach (var u in users) device.Touch(u, T0);
        Assert.Equal(TrustedDevice.MaxUsers, device.UserIds.Count);
        Assert.False(device.IsTrustedFor(users[0]));
        Assert.True(device.IsTrustedFor(users[^1]));

        device.Touch(users[5], T0.AddMinutes(1));
        Assert.Equal(users[5], device.UserIds[^1]);
        Assert.Equal(T0.AddMinutes(1), device.LastSeenAt);
    }
}
