namespace Modgud.Application.Dcr;

/// <summary>
/// Rate limiter for <c>/connect/register</c>: per source address (spray-from-one-IP
/// protection) and per realm (caps storage growth when an attacker rotates addresses).
/// Limits come from the realm's <c>DcrSettings</c> on every call, so a patched setting
/// applies to the next request. ADR 0019: backed by the shared Postgres counters, so
/// every Modgud instance agrees on the count.
/// </summary>
public interface IDcrRateLimiter
{
    Task<DcrRateLimitVerdict> TryConsumeAsync(
        string sourceIp, string realmSlug, int perIpLimit, int perRealmLimit, CancellationToken ct = default);
}

public enum DcrRateLimitVerdict
{
    Allowed,
    PerIpExceeded,
    PerRealmExceeded,
}
