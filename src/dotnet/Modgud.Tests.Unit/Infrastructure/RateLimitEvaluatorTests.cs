using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Modgud.Authentication.RateLimiting;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.RateLimiting;

namespace Modgud.Tests.Unit.Infrastructure;

/// <summary>ADR 0019 — dimension roles, forwarder semantics, NAT sizing, log-only.</summary>
public class RateLimitEvaluatorTests
{
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);

    private static (RateLimitEvaluator Evaluator, TestClock Clock, InMemoryRateLimitStore Store) Build()
    {
        var clock = new TestClock(T0);
        var store = new InMemoryRateLimitStore();
        return (new RateLimitEvaluator(store, NullLogger<RateLimitEvaluator>.Instance, clock), clock, store);
    }

    private static AuthCallerContext Caller(string source = "203.0.113.10", Guid? app = null, string? clientId = null,
        bool allowlisted = false, IPAddress? forwarded = null) => new()
    {
        RealmSlug = "acme",
        ApplicationId = app,
        ClientId = clientId,
        ClientIsConfidential = clientId is not null,
        RemoteAddress = IPAddress.Parse("198.51.100.1"),
        ForwardedAddress = forwarded,
        SourceKey = forwarded is not null ? AuthCallerContext.SourceKeyFor(forwarded) : source,
        SourceAllowlisted = allowlisted,
    };

    private static AuthRateLimitSettings Limits(AuthRateLimitPolicy policy, PolicyLimits limits) => new()
    {
        Policies = new Dictionary<string, PolicyLimits> { [AuthRateLimitDefaults.PolicyName(policy)] = limits },
    };

    [Fact]
    public async Task Target_is_limited_regardless_of_rotating_sources()
    {
        var (sut, _, _) = Build();
        var settings = Limits(AuthRateLimitPolicy.NativeOtp, new PolicyLimits { Target = RateLimitRule.Fixed(3, 60) });

        for (var i = 0; i < 3; i++)
        {
            var d = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller($"10.0.{i}.1"), settings, "Victim@Example.test", null);
            Assert.Equal(RateLimitOutcome.Allow, d.Outcome);
        }
        var rejected = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller("10.9.9.9"), settings, "victim@example.test", null);
        Assert.Equal(RateLimitOutcome.Reject, rejected.Outcome);
        Assert.Equal(RateLimitDimension.Target, rejected.Dimension);
        Assert.True(rejected.RetryAfterSeconds > 0);
    }

    [Fact]
    public async Task A_thousand_known_users_behind_one_nat_are_not_rejected_at_defaults()
    {
        var (sut, clock, _) = Build();
        // 1000 distinct mailboxes, one source, spread over one office hour.
        for (var i = 0; i < 1000; i++)
        {
            clock.Now = T0 + TimeSpan.FromSeconds(i * 3.6);
            var d = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller(), null, $"user{i}@office.example", null);
            Assert.Equal(RateLimitOutcome.Allow, d.Outcome);
        }
    }

    [Fact]
    public async Task Source_bucket_still_stops_a_flood_from_one_address()
    {
        var (sut, _, _) = Build();
        var rejections = 0;
        for (var i = 0; i < 2000; i++)
        {
            var d = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller(), null, $"user{i}@office.example", null);
            if (d.Outcome == RateLimitOutcome.Reject) { rejections++; Assert.Equal(RateLimitDimension.Source, d.Dimension); }
        }
        Assert.True(rejections > 1000, $"expected the burst to run dry, got {rejections} rejections");
    }

    [Fact]
    public async Task A_forwarder_shifts_only_the_source_dimension()
    {
        var (sut, _, _) = Build();
        var settings = Limits(AuthRateLimitPolicy.NativeOtp, new PolicyLimits
        {
            Source = RateLimitRule.Fixed(1, 60),
            Client = RateLimitRule.Fixed(3, 60),
        });

        // Two browsers behind one BFF: separate source buckets.
        var a = Caller(clientId: "bff", forwarded: IPAddress.Parse("10.1.1.1"));
        var b = Caller(clientId: "bff", forwarded: IPAddress.Parse("10.1.1.2"));
        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, a, settings, "a@x.test", null)).Outcome);
        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, b, settings, "b@x.test", null)).Outcome);
        var again = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, a, settings, "a2@x.test", null);
        Assert.Equal(RateLimitDimension.Source, again.Dimension);

        // …but the client ceiling still bounds the BFF as a whole (2 spent + this one = 3, next rejects).
        var c = Caller(clientId: "bff", forwarded: IPAddress.Parse("10.1.1.3"));
        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, c, settings, "c@x.test", null)).Outcome);
        var d = Caller(clientId: "bff", forwarded: IPAddress.Parse("10.1.1.4"));
        var overClient = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, d, settings, "d@x.test", null);
        Assert.Equal(RateLimitOutcome.Reject, overClient.Outcome);
        Assert.Equal(RateLimitDimension.Client, overClient.Dimension);
    }

    [Fact]
    public async Task Allowlisted_source_skips_source_dimensions_but_target_and_app_apply()
    {
        var (sut, _, _) = Build();
        var settings = Limits(AuthRateLimitPolicy.NativeOtp, new PolicyLimits
        {
            Source = RateLimitRule.Fixed(1, 60),
            SourceRegistration = RateLimitRule.Fixed(1, 60),
            Target = RateLimitRule.Fixed(2, 60),
        });
        var office = Caller(allowlisted: true);

        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, office, settings, "one@x.test", null)).Outcome);
        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, office, settings, "two@x.test", null)).Outcome);
        Assert.True(await sut.AllowRegistrationEntryAsync(AuthRateLimitPolicy.NativeOtp, office, settings));
        Assert.True(await sut.AllowRegistrationEntryAsync(AuthRateLimitPolicy.NativeOtp, office, settings));

        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, office, settings, "same@x.test", null)).Outcome);
        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, office, settings, "same@x.test", null)).Outcome);
        var third = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, office, settings, "same@x.test", null);
        Assert.Equal(RateLimitDimension.Target, third.Dimension);
    }

    [Fact]
    public async Task Registration_entries_per_source_go_silent_after_the_ceiling()
    {
        var (sut, _, _) = Build();
        var settings = Limits(AuthRateLimitPolicy.NativeOtp, new PolicyLimits { SourceRegistration = RateLimitRule.Fixed(2, 60) });
        var sprayer = Caller();

        Assert.True(await sut.AllowRegistrationEntryAsync(AuthRateLimitPolicy.NativeOtp, sprayer, settings));
        Assert.True(await sut.AllowRegistrationEntryAsync(AuthRateLimitPolicy.NativeOtp, sprayer, settings));
        Assert.False(await sut.AllowRegistrationEntryAsync(AuthRateLimitPolicy.NativeOtp, sprayer, settings));
        // Loud dimensions were never charged by the silent check.
        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, sprayer, settings, "x@x.test", null)).Outcome);
    }

    [Fact]
    public async Task Log_only_mode_counts_but_never_rejects()
    {
        var (sut, _, _) = Build();
        var settings = Limits(AuthRateLimitPolicy.NativeOtp, new PolicyLimits { Source = RateLimitRule.Fixed(1, 60) }) with
        {
            Mode = RateLimitEnforcementMode.LogOnly,
        };
        for (var i = 0; i < 5; i++)
        {
            var d = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller(), settings, $"u{i}@x.test", null);
            Assert.Equal(RateLimitOutcome.Allow, d.Outcome);
            Assert.True(d.LogOnly);
        }
        var silentSettings = Limits(AuthRateLimitPolicy.NativeOtp, new PolicyLimits { SourceRegistration = RateLimitRule.Fixed(1, 60) }) with { Mode = RateLimitEnforcementMode.LogOnly };
        Assert.True(await sut.AllowRegistrationEntryAsync(AuthRateLimitPolicy.NativeOtp, Caller("1.1.1.1"), silentSettings));
        Assert.True(await sut.AllowRegistrationEntryAsync(AuthRateLimitPolicy.NativeOtp, Caller("1.1.1.1"), silentSettings));
    }

    [Fact]
    public async Task Legacy_per_ip_overrides_put_a_realm_in_log_only_until_a_mode_is_chosen()
    {
        var legacy = new AuthRateLimitSettings { NativeOtp = RateLimitRule.Fixed(1, 60) };
        Assert.Equal(RateLimitEnforcementMode.LogOnly, AuthRateLimitSettings.EffectiveMode(legacy));
        Assert.Equal(RateLimitEnforcementMode.Enforce, AuthRateLimitSettings.EffectiveMode(legacy with { Mode = RateLimitEnforcementMode.Enforce }));
        Assert.Equal(RateLimitEnforcementMode.Enforce, AuthRateLimitSettings.EffectiveMode(null));
        // The legacy value is NOT the source ceiling.
        Assert.Equal(AuthRateLimitDefaults.For(AuthRateLimitPolicy.NativeOtp, RateLimitDimension.Source),
            AuthRateLimitSettings.Effective(legacy, AuthRateLimitPolicy.NativeOtp, RateLimitDimension.Source));

        var (sut, _, _) = Build();
        var d = await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller(), legacy, "a@x.test", null);
        Assert.True(d.LogOnly);
    }

    [Fact]
    public async Task Dimensions_that_do_not_apply_are_skipped()
    {
        var (sut, _, _) = Build();
        // bootstrap has only a source ceiling; a target never trips anything.
        for (var i = 0; i < 20; i++)
        {
            var d = await sut.EvaluateAsync(AuthRateLimitPolicy.Bootstrap, Caller($"10.0.0.{i}"), null, "same-token", null);
            Assert.Equal(RateLimitOutcome.Allow, d.Outcome);
        }
    }

    [Fact]
    public void Ipv6_sources_share_a_bucket_per_64_prefix()
    {
        var a = AuthCallerContext.SourceKeyFor(IPAddress.Parse("2001:db8:1:2::1"));
        var b = AuthCallerContext.SourceKeyFor(IPAddress.Parse("2001:db8:1:2:ffff::9"));
        var c = AuthCallerContext.SourceKeyFor(IPAddress.Parse("2001:db8:1:3::1"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.EndsWith("/64", a);
        Assert.Equal("203.0.113.7", AuthCallerContext.SourceKeyFor(IPAddress.Parse("203.0.113.7")));
        Assert.Equal("203.0.113.7", AuthCallerContext.SourceKeyFor(IPAddress.Parse("::ffff:203.0.113.7")));
    }

    [Fact]
    public void Allowlist_matches_cidr_ranges_and_single_addresses()
    {
        Assert.True(AuthCallerContextFactory.IsAllowlisted(IPAddress.Parse("10.20.30.40"), ["10.20.0.0/16"]));
        Assert.False(AuthCallerContextFactory.IsAllowlisted(IPAddress.Parse("10.21.30.40"), ["10.20.0.0/16"]));
        Assert.True(AuthCallerContextFactory.IsAllowlisted(IPAddress.Parse("203.0.113.9"), ["203.0.113.9"]));
        Assert.True(AuthCallerContextFactory.IsAllowlisted(IPAddress.Parse("2001:db8::5"), ["2001:db8::/32"]));
        Assert.False(AuthCallerContextFactory.IsAllowlisted(IPAddress.Parse("1.2.3.4"), []));
        Assert.False(AuthCallerContextFactory.IsAllowlisted(IPAddress.Parse("1.2.3.4"), ["not-an-address"]));
    }

    [Fact]
    public void Fixed_window_and_bucket_math()
    {
        var rule = RateLimitRule.Fixed(5, 60);
        var now = new DateTimeOffset(2026, 9, 4, 9, 17, 30, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero), RateLimitMath.WindowStart(now, rule));
        Assert.Equal(42 * 60 + 30, RateLimitMath.RetryAfterForWindow(now, rule));

        var bucket = RateLimitRule.Bucket(1200, 60, 300);
        var (capacity, rate) = RateLimitMath.Bucket(bucket);
        Assert.Equal(300, capacity);
        Assert.Equal(1200.0 / 3600.0, rate, 6);
        Assert.Equal(3, RateLimitMath.RetryAfterForBucket(0.2, bucket));
    }

    [Fact]
    public async Task Token_bucket_absorbs_a_burst_then_refills()
    {
        var (sut, clock, _) = Build();
        var settings = Limits(AuthRateLimitPolicy.NativeOtp, new PolicyLimits { Source = RateLimitRule.Bucket(60, 1, 5) });
        for (var i = 0; i < 5; i++)
            Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller(), settings, $"b{i}@x.test", null)).Outcome);
        Assert.Equal(RateLimitOutcome.Reject, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller(), settings, "b5@x.test", null)).Outcome);
        clock.Now = T0 + TimeSpan.FromSeconds(2); // 1 token/second refill
        Assert.Equal(RateLimitOutcome.Allow, (await sut.EvaluateAsync(AuthRateLimitPolicy.NativeOtp, Caller(), settings, "b6@x.test", null)).Outcome);
    }
}
