using Microsoft.Extensions.DependencyInjection;
using Modgud.Api.Tests.Infrastructure;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.RateLimiting;

namespace Modgud.Api.Tests.Authorization;

/// <summary>
/// B8-AJ-04 — a request captures its time before its hits, so a slower request can
/// reach the counter after a later one. Such a late hit must not reset the newer
/// window nor move the bucket's clock back (which refilled the same interval twice).
/// Both stores share the arithmetic, so both are pinned.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class RateLimitStoreClockTests(SharedPostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("memory")]
    public async Task A_late_hit_for_the_previous_window_does_not_reset_the_current_one(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(kind);
        var scope = new RateLimitScope("system");
        var key = $"test|late-window|{Guid.NewGuid():N}";
        var rule = RateLimitRule.Fixed(2, 1);
        var boundary = RateLimitMath.WindowStart(DateTimeOffset.UtcNow, rule).AddMinutes(1);

        Assert.True((await store.HitAsync(scope, key, rule, boundary.AddSeconds(10), ct)).Allowed);
        Assert.True((await store.HitAsync(scope, key, rule, boundary.AddSeconds(10), ct)).Allowed);

        // Captured before the boundary, counted after it: one more hit of the current window.
        Assert.False((await store.HitAsync(scope, key, rule, boundary.AddSeconds(-10), ct)).Allowed);
        Assert.False((await store.HitAsync(scope, key, rule, boundary.AddSeconds(10), ct)).Allowed);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("memory")]
    public async Task A_late_token_bucket_hit_does_not_move_the_refill_clock_back(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store(kind);
        var scope = new RateLimitScope("system");
        var key = $"test|late-bucket|{Guid.NewGuid():N}";
        var rule = RateLimitRule.Bucket(60, 1, 2); // capacity 2, one token per second
        var t0 = DateTimeOffset.UtcNow;

        Assert.True((await store.HitAsync(scope, key, rule, t0, ct)).Allowed);
        Assert.True((await store.HitAsync(scope, key, rule, t0.AddSeconds(10), ct)).Allowed);
        Assert.True((await store.HitAsync(scope, key, rule, t0, ct)).Allowed); // late: the last token

        // No time passed since t0+10: nothing to refill, the bucket is empty.
        Assert.False((await store.HitAsync(scope, key, rule, t0.AddSeconds(10), ct)).Allowed);
    }

    private IRateLimitStore Store(string kind) => kind == "postgres"
        ? new PostgresRateLimitStore(Factory.Services.GetRequiredService<IRateLimitConnectionSource>())
        : new InMemoryRateLimitStore();
}
