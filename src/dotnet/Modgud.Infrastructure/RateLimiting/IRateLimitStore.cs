using Modgud.Domain.Realms;
using Npgsql;

namespace Modgud.Infrastructure.RateLimiting;

/// <summary>Result of charging one request against one bucket.</summary>
public readonly record struct RateLimitHit(bool Allowed, int RetryAfterSeconds);

/// <summary>Which database owns the counter: a realm's tenant DB, or the global
/// store for realm-independent policies (installation / bootstrap).</summary>
public readonly record struct RateLimitScope(string? TenantId)
{
    public static RateLimitScope Global => new((string?)null);
}

/// <summary>
/// ADR 0007 — shared, multi-instance-correct counters. One atomic upsert per hit;
/// fixed windows for "N per window" semantics, token buckets (capacity = burst,
/// refill = limit per window) where a legitimate peak must be absorbed.
/// </summary>
public interface IRateLimitStore
{
    Task<RateLimitHit> HitAsync(RateLimitScope scope, string key, RateLimitRule rule, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Deletes buckets untouched since <paramref name="olderThan"/>. Returns the count.</summary>
    Task<int> PruneAsync(RateLimitScope scope, DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>Opens a connection to the database that owns a scope's counters.</summary>
public interface IRateLimitConnectionSource
{
    Task<NpgsqlConnection> OpenAsync(RateLimitScope scope, CancellationToken ct = default);
}

/// <summary>Pure limiter arithmetic shared by every store implementation.</summary>
public static class RateLimitMath
{
    public static TimeSpan Window(RateLimitRule rule) => TimeSpan.FromMinutes(Math.Max(1, rule.WindowMinutes));

    /// <summary>Token bucket: capacity is the burst, refill rate is the limit per window.</summary>
    public static (double Capacity, double RefillPerSecond) Bucket(RateLimitRule rule)
    {
        var capacity = Math.Max(1, rule.Burst ?? rule.PermitLimit);
        var perSecond = Math.Max(1, rule.PermitLimit) / Window(rule).TotalSeconds;
        return (capacity, perSecond);
    }

    public static DateTimeOffset WindowStart(DateTimeOffset now, RateLimitRule rule)
    {
        var w = Window(rule).Ticks;
        return new DateTimeOffset(now.UtcTicks / w * w, TimeSpan.Zero);
    }

    public static int RetryAfterForWindow(DateTimeOffset now, RateLimitRule rule) =>
        Math.Max(1, (int)Math.Ceiling((WindowStart(now, rule) + Window(rule) - now).TotalSeconds));

    public static int RetryAfterForBucket(double tokens, RateLimitRule rule)
    {
        var (_, rate) = Bucket(rule);
        return Math.Max(1, (int)Math.Ceiling((1 - tokens) / rate));
    }
}
