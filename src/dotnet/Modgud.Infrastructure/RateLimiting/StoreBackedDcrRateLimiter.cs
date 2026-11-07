using System.Net;
using Modgud.Application.Dcr;
using Modgud.Domain.Realms;

namespace Modgud.Infrastructure.RateLimiting;

/// <summary>DCR registration limits on the shared counters: source per hour, realm per day.</summary>
public sealed class StoreBackedDcrRateLimiter(IRateLimitStore store) : IDcrRateLimiter
{
    public async Task<DcrRateLimitVerdict> TryConsumeAsync(
        string sourceIp, string realmSlug, int perIpLimit, int perRealmLimit, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var scope = new RateLimitScope(realmSlug);

        // The same source key as every other auth limit: an IPv6 /64 is one source (B8-AJ-05).
        var sourceKey = IPAddress.TryParse(sourceIp, out var address) ? AuthCallerContext.SourceKeyFor(address) : sourceIp;
        var perIp = await store.HitAsync(scope, $"dcr-register|source|{realmSlug}|{sourceKey}",
            RateLimitRule.Fixed(Math.Max(1, perIpLimit), 60), now, ct);
        if (!perIp.Allowed) return DcrRateLimitVerdict.PerIpExceeded;

        var perRealm = await store.HitAsync(scope, $"dcr-register|app|{realmSlug}|realm",
            RateLimitRule.Fixed(Math.Max(1, perRealmLimit), 1440), now, ct);
        return perRealm.Allowed ? DcrRateLimitVerdict.Allowed : DcrRateLimitVerdict.PerRealmExceeded;
    }
}
