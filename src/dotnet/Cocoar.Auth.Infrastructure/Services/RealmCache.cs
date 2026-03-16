using System.Collections.Concurrent;
using Cocoar.Auth.Domain.Entities;
using Marten;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Cache of active realm slugs for fast middleware validation.
/// </summary>
public interface IRealmCache
{
	Task<bool> IsValidRealmAsync(string slug);
	void Invalidate();
	Task InitializeAsync(CancellationToken ct = default);
}

public class RealmCache : IRealmCache
{
	private readonly IDocumentStore _store;
	private volatile ConcurrentDictionary<string, bool>? _cache;

	private const string SystemTenantId = "system";

	public RealmCache(IDocumentStore store)
	{
		_store = store;
	}

	public async Task<bool> IsValidRealmAsync(string slug)
	{
		// "system" is always valid
		if (string.Equals(slug, "system", StringComparison.OrdinalIgnoreCase))
			return true;

		var cache = _cache;
		if (cache is null)
		{
			await LoadCacheAsync();
			cache = _cache;
		}

		return cache?.ContainsKey(slug) == true;
	}

	public void Invalidate()
	{
		_cache = null;
	}

	public async Task InitializeAsync(CancellationToken ct = default)
	{
		await LoadCacheAsync(ct);
	}

	private async Task LoadCacheAsync(CancellationToken ct = default)
	{
		var newCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		newCache["system"] = true;

		await using var session = _store.QuerySession(SystemTenantId);
		var activeRealms = await session.Query<Realm>()
			.Where(r => r.IsActive)
			.ToListAsync(ct);

		foreach (var realm in activeRealms)
		{
			newCache[realm.Slug] = true;
		}

		_cache = newCache;
	}
}
