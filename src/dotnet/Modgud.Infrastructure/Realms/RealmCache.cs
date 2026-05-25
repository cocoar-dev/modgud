using System.Collections.Concurrent;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;

namespace Modgud.Infrastructure.Realms;

/// <summary>
/// Resolved tenant information from the realm cache.
/// Stored in <c>HttpContext.Items["TenantInfo"]</c> by <c>RealmMiddleware</c>.
/// </summary>
public sealed record TenantInfo(string Slug, bool IsControlPlane, bool IsActive);

/// <summary>
/// Cache of domain → realm mappings for fast middleware resolution.
/// Loaded lazily from the global store and invalidated on realm CUD.
/// </summary>
public interface IRealmCache
{
    /// <summary>
    /// Returns the realm whose <see cref="Realm.Domains"/> contain the given hostname,
    /// or <c>null</c> when no active realm matches.
    /// Single-tenant fallback: when only one active realm exists and the hostname is
    /// a localhost variant, we return that realm so dev boots work without hosts entries.
    /// </summary>
    Task<TenantInfo?> ResolveDomainAsync(string hostname);

    /// <summary>
    /// Returns every active realm. Used by hosted services that need to enumerate
    /// tenants at boot — e.g. <c>OidcSchemeBootstrap</c> which has to register
    /// external login providers from every realm, not just the system realm
    /// (WOLV-02).
    /// </summary>
    Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync();

    void Invalidate();

    Task InitializeAsync(CancellationToken ct = default);
}

public sealed class RealmCache : IRealmCache
{
    private readonly IGlobalStore _globalStore;

    private volatile CacheSnapshot? _snapshot;

    private sealed record CacheSnapshot(
        ConcurrentDictionary<string, TenantInfo> ByDomain,
        TenantInfo? SingleActiveRealm);

    public RealmCache(IGlobalStore globalStore)
    {
        _globalStore = globalStore;
    }

    public async Task<TenantInfo?> ResolveDomainAsync(string hostname)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            await LoadCacheAsync();
            snapshot = _snapshot;
        }

        if (snapshot is null)
            return null;

        // Dev-friendly fallback: localhost-style host with exactly one active realm
        // → return that realm. Lets the existing single-tenant boot keep working
        // without requiring a hosts-file entry for system.localhost.
        return RealmCacheLookup.Resolve(hostname, snapshot.ByDomain, snapshot.SingleActiveRealm);
    }

    public async Task<IReadOnlyList<TenantInfo>> GetAllActiveAsync()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            await LoadCacheAsync();
            snapshot = _snapshot;
        }

        if (snapshot is null)
            return Array.Empty<TenantInfo>();

        // Distinct by Slug because the by-domain map can carry the same
        // TenantInfo under multiple domain keys (e.g. "system.localhost",
        // "localhost", "127.0.0.1" all resolve to the system realm).
        return snapshot.ByDomain.Values
            .GroupBy(t => t.Slug)
            .Select(g => g.First())
            .ToList();
    }

    public void Invalidate()
    {
        _snapshot = null;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await LoadCacheAsync(ct);
    }

    private async Task LoadCacheAsync(CancellationToken ct = default)
    {
        var byDomain = new ConcurrentDictionary<string, TenantInfo>(StringComparer.OrdinalIgnoreCase);

        await using var session = _globalStore.QuerySession();
        var activeRealms = await session.Query<Realm>()
            .Where(r => r.IsActive)
            .ToListAsync(ct);

        foreach (var realm in activeRealms)
        {
            var info = new TenantInfo(realm.Slug, realm.IsControlPlane, realm.IsActive);
            foreach (var domain in realm.Domains)
            {
                byDomain[domain] = info;
            }
        }

        TenantInfo? single = null;
        if (activeRealms.Count == 1)
        {
            var only = activeRealms[0];
            single = new TenantInfo(only.Slug, only.IsControlPlane, only.IsActive);
        }

        _snapshot = new CacheSnapshot(byDomain, single);
    }
}
