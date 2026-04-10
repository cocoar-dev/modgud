using System.Collections.Concurrent;
using Cocoar.Auth.Domain.Entities;
using Marten;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Resolved tenant information from the domain cache.
/// Stored in HttpContext.Items["TenantInfo"] by the middleware.
/// </summary>
public record TenantInfo(string Slug, bool CanManageTenants, bool IsActive);

/// <summary>
/// Cache of domain → tenant mappings for fast middleware resolution.
/// </summary>
public interface IRealmCache
{
	Task<TenantInfo?> ResolveDomainAsync(string hostname);
	void Invalidate();
	Task InitializeAsync(CancellationToken ct = default);
}

public class RealmCache : IRealmCache
{
	private readonly IDocumentStore _store;
	private volatile ConcurrentDictionary<string, TenantInfo>? _domainCache;

	private const string SystemTenantId = "system";

	public RealmCache(IDocumentStore store)
	{
		_store = store;
	}

	public async Task<TenantInfo?> ResolveDomainAsync(string hostname)
	{
		var cache = _domainCache;
		if (cache is null)
		{
			await LoadCacheAsync();
			cache = _domainCache;
		}

		if (cache is not null && cache.TryGetValue(hostname, out var info))
			return info;

		return null;
	}

	public void Invalidate()
	{
		_domainCache = null;
	}

	public async Task InitializeAsync(CancellationToken ct = default)
	{
		await LoadCacheAsync(ct);
	}

	private async Task LoadCacheAsync(CancellationToken ct = default)
	{
		var newCache = new ConcurrentDictionary<string, TenantInfo>(StringComparer.OrdinalIgnoreCase);

		await using var session = _store.QuerySession(SystemTenantId);
		var activeRealms = await session.Query<Realm>()
			.Where(r => r.IsActive)
			.ToListAsync(ct);

		foreach (var realm in activeRealms)
		{
			var info = new TenantInfo(realm.Slug, realm.CanManageTenants, realm.IsActive);
			foreach (var domain in realm.Domains)
			{
				newCache[domain] = info;
			}
		}

		_domainCache = newCache;
	}
}
