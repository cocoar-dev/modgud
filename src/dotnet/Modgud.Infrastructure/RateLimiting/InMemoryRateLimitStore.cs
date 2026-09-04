using System.Collections.Concurrent;
using Modgud.Domain.Realms;

namespace Modgud.Infrastructure.RateLimiting;

/// <summary>Process-local twin of <see cref="PostgresRateLimitStore"/> for unit tests
/// (same arithmetic, no database). Never registered in a running Modgud.</summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private sealed class Bucket
    {
        public DateTimeOffset? WindowStart;
        public int Hits;
        public double? Tokens;
        public DateTimeOffset UpdatedAt;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public Task<RateLimitHit> HitAsync(RateLimitScope scope, string key, RateLimitRule rule, DateTimeOffset now, CancellationToken ct = default)
    {
        var bucket = _buckets.GetOrAdd($"{scope.TenantId ?? "-"}|{key}", _ => new Bucket { UpdatedAt = now });
        lock (bucket)
        {
            if (rule.IsTokenBucket)
            {
                var (capacity, rate) = RateLimitMath.Bucket(rule);
                var elapsed = Math.Max(0, (now - bucket.UpdatedAt).TotalSeconds);
                var refilled = Math.Min(capacity, (bucket.Tokens ?? capacity) + elapsed * rate);
                bucket.UpdatedAt = now;
                if (refilled >= 1)
                {
                    bucket.Tokens = refilled - 1;
                    return Task.FromResult(new RateLimitHit(true, 0));
                }
                bucket.Tokens = refilled;
                return Task.FromResult(new RateLimitHit(false, RateLimitMath.RetryAfterForBucket(refilled, rule)));
            }

            var ws = RateLimitMath.WindowStart(now, rule);
            if (bucket.WindowStart != ws) { bucket.WindowStart = ws; bucket.Hits = 0; }
            bucket.Hits++;
            bucket.UpdatedAt = now;
            return Task.FromResult(bucket.Hits <= Math.Max(1, rule.PermitLimit)
                ? new RateLimitHit(true, 0)
                : new RateLimitHit(false, RateLimitMath.RetryAfterForWindow(now, rule)));
        }
    }

    public Task<int> PruneAsync(RateLimitScope scope, DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.UpdatedAt < olderThan && _buckets.TryRemove(key, out _)) removed++;
        }
        return Task.FromResult(removed);
    }
}
