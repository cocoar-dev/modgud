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

        var perIp = await store.HitAsync(scope, $"dcr-register|source|{realmSlug}|{sourceIp}",
            RateLimitRule.Fixed(Math.Max(1, perIpLimit), 60), now, ct);
        if (!perIp.Allowed) return DcrRateLimitVerdict.PerIpExceeded;

        var perRealm = await store.HitAsync(scope, $"dcr-register|app|{realmSlug}|realm",
            RateLimitRule.Fixed(Math.Max(1, perRealmLimit), 1440), now, ct);
        return perRealm.Allowed ? DcrRateLimitVerdict.Allowed : DcrRateLimitVerdict.PerRealmExceeded;
    }
}
