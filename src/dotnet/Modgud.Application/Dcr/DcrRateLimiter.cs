using System.Collections.Concurrent;

namespace Modgud.Application.Dcr;

/// <summary>
/// In-memory rate-limiter for <c>/connect/register</c>. Tracks two
/// independent rolling-window counters:
/// <list type="bullet">
///   <item>Per-source-IP, default 5/h. Spray-from-one-IP protection.</item>
///   <item>Per-realm, default 100/d. Caps storage growth if an attacker
///         rotates IPs faster than the per-IP window resets.</item>
/// </list>
///
/// <para>Limits are configured per-realm via <c>DcrSettings</c> — every
/// <see cref="TryConsume"/> call passes the resolved settings, so a
/// patched setting takes effect on the next request without restart.</para>
///
/// <para>Resets on process restart. Acceptable for v1: restart isn't a
/// useful spray-cycle bypass (Cocoar restarts are infrequent and
/// observable), and a durable counter would need a new persistence path
/// for marginal benefit. The
/// <c>cocoar:dcr:registered_at</c> property on minted clients is the
/// audit-trail fallback.</para>
///
/// <para>Both lookups use ordinal-case-sensitive keys: source IPs are
/// already normalised by the endpoint layer, and realm slugs are the
/// stable string keys (matches the tenant id Marten uses). Earlier
/// drafts used Guid for the realm key, but
/// <c>HttpContext.Items["TenantId"]</c> is set to the slug by
/// <c>RealmMiddleware</c> — a parse-to-Guid attempt would collapse
/// every realm onto <c>Guid.Empty</c> and turn the per-realm limit
/// into a global one shared across all tenants.</para>
/// </summary>
public sealed class DcrRateLimiter
{
    private readonly ConcurrentDictionary<string, RingBuffer> _perIp = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RingBuffer> _perRealm = new(StringComparer.Ordinal);

    private static readonly TimeSpan IpWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan RealmWindow = TimeSpan.FromDays(1);

    public DcrRateLimitVerdict TryConsume(
        string sourceIp, string realmSlug, int perIpLimit, int perRealmLimit)
    {
        var now = DateTimeOffset.UtcNow;

        var ipBuffer = _perIp.GetOrAdd(sourceIp, _ => new RingBuffer());
        var realmBuffer = _perRealm.GetOrAdd(realmSlug, _ => new RingBuffer());

        // Check both windows BEFORE committing to either, so a realm-limit
        // hit doesn't pre-charge the per-IP counter (which would
        // double-punish a single IP that's still under its own limit).
        lock (ipBuffer)
        {
            ipBuffer.Trim(now - IpWindow);
            if (ipBuffer.Count >= perIpLimit) return DcrRateLimitVerdict.PerIpExceeded;
        }
        lock (realmBuffer)
        {
            realmBuffer.Trim(now - RealmWindow);
            if (realmBuffer.Count >= perRealmLimit) return DcrRateLimitVerdict.PerRealmExceeded;
        }

        lock (ipBuffer) ipBuffer.Add(now);
        lock (realmBuffer) realmBuffer.Add(now);
        return DcrRateLimitVerdict.Allowed;
    }

    private sealed class RingBuffer
    {
        private readonly List<DateTimeOffset> _stamps = new();
        public int Count => _stamps.Count;
        public void Trim(DateTimeOffset cutoff) => _stamps.RemoveAll(t => t < cutoff);
        public void Add(DateTimeOffset t) => _stamps.Add(t);
    }
}

public enum DcrRateLimitVerdict
{
    Allowed,
    PerIpExceeded,
    PerRealmExceeded,
}
