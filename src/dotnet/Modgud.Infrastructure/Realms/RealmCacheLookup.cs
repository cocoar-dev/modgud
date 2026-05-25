namespace Modgud.Infrastructure.Realms;

/// <summary>
/// Pure (no-IO) lookup logic shared by <see cref="RealmCache"/>: given a precomputed
/// in-memory snapshot of the active realms, decide which <see cref="TenantInfo"/>
/// (if any) a request hostname maps to.
/// <para>
/// Extracted from <see cref="RealmCache"/> so the host-matching + localhost-fallback
/// rules can be unit-tested without spinning up an <c>IGlobalStore</c>.
/// </para>
/// </summary>
public static class RealmCacheLookup
{
    /// <summary>
    /// Hostnames considered "the local dev box" for purposes of the single-realm
    /// fallback. Matched case-insensitively.
    /// </summary>
    public static readonly IReadOnlySet<string> LocalhostHosts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            "127.0.0.1",
            "0.0.0.0",
            "::1",
        };

    /// <summary>
    /// Returns the realm whose configured domains contain <paramref name="hostname"/>,
    /// or — when there is exactly one active realm in the system and the request
    /// targets a localhost variant — that single realm. Returns <c>null</c> in every
    /// other case (multi-realm misses, no realms at all, unknown hosts).
    /// </summary>
    /// <param name="hostname">The request's <c>Host</c>-header host (no port).</param>
    /// <param name="byDomain">Domain → tenant lookup. Comparer is the caller's responsibility.</param>
    /// <param name="singleActiveRealm">
    /// The only active realm, or <c>null</c> if there are zero or two-or-more active realms.
    /// </param>
    public static TenantInfo? Resolve(
        string hostname,
        IReadOnlyDictionary<string, TenantInfo> byDomain,
        TenantInfo? singleActiveRealm)
    {
        ArgumentNullException.ThrowIfNull(hostname);
        ArgumentNullException.ThrowIfNull(byDomain);

        if (byDomain.TryGetValue(hostname, out var info))
            return info;

        if (singleActiveRealm is not null && LocalhostHosts.Contains(hostname))
            return singleActiveRealm;

        return null;
    }
}
