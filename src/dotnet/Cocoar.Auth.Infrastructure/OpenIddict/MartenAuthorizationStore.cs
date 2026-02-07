using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cocoar.Auth.Domain.Entities;
using Marten;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict authorization store using Marten document storage.
/// </summary>
public class MartenAuthorizationStore : IOpenIddictAuthorizationStore<OpenIddictAuthorizationDocument>
{
	private readonly IDocumentStore _store;

	public MartenAuthorizationStore(IDocumentStore store)
	{
		_store = store;
	}

	public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.Query<OpenIddictAuthorizationDocument>().CountAsync(cancellationToken);
	}

	public async ValueTask<long> CountAsync<TResult>(
		Func<IQueryable<OpenIddictAuthorizationDocument>, IQueryable<TResult>> query,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return query(session.Query<OpenIddictAuthorizationDocument>()).LongCount();
	}

	public async ValueTask CreateAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		session.Store(authorization);
		await session.SaveChangesAsync(cancellationToken);
	}

	public async ValueTask DeleteAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		session.Delete(authorization);
		await session.SaveChangesAsync(cancellationToken);
	}

	public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
		string? subject,
		string? client,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

		if (!string.IsNullOrEmpty(subject))
		{
			query = query.Where(x => x.Subject == subject);
		}

		if (!string.IsNullOrEmpty(client))
		{
			query = query.Where(x => x.ApplicationId == client);
		}

		var authorizations = await query.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			yield return authorization;
		}
	}

	public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
		string? subject,
		string? client,
		string? status,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

		if (!string.IsNullOrEmpty(subject))
		{
			query = query.Where(x => x.Subject == subject);
		}

		if (!string.IsNullOrEmpty(client))
		{
			query = query.Where(x => x.ApplicationId == client);
		}

		if (!string.IsNullOrEmpty(status))
		{
			query = query.Where(x => x.Status == status);
		}

		var authorizations = await query.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			yield return authorization;
		}
	}

	public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
		string? subject,
		string? client,
		string? status,
		string? type,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

		if (!string.IsNullOrEmpty(subject))
		{
			query = query.Where(x => x.Subject == subject);
		}

		if (!string.IsNullOrEmpty(client))
		{
			query = query.Where(x => x.ApplicationId == client);
		}

		if (!string.IsNullOrEmpty(status))
		{
			query = query.Where(x => x.Status == status);
		}

		if (!string.IsNullOrEmpty(type))
		{
			query = query.Where(x => x.Type == type);
		}

		var authorizations = await query.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			yield return authorization;
		}
	}

	public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
		string? subject,
		string? client,
		string? status,
		string? type,
		ImmutableArray<string>? scopes,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

		if (!string.IsNullOrEmpty(subject))
		{
			query = query.Where(x => x.Subject == subject);
		}

		if (!string.IsNullOrEmpty(client))
		{
			query = query.Where(x => x.ApplicationId == client);
		}

		if (!string.IsNullOrEmpty(status))
		{
			query = query.Where(x => x.Status == status);
		}

		if (!string.IsNullOrEmpty(type))
		{
			query = query.Where(x => x.Type == type);
		}

		var authorizations = await query.ToListAsync(cancellationToken);

		// Filter by scopes in memory (Marten doesn't support complex set operations)
		foreach (var authorization in authorizations)
		{
			if (scopes is null || scopes.Value.IsDefault || scopes.Value.All(s => authorization.Scopes.Contains(s)))
			{
				yield return authorization;
			}
		}
	}

	public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindByApplicationIdAsync(
		string identifier,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
			.Where(x => x.ApplicationId == identifier)
			.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			yield return authorization;
		}
	}

	public async ValueTask<OpenIddictAuthorizationDocument?> FindByIdAsync(
		string identifier,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.LoadAsync<OpenIddictAuthorizationDocument>(identifier, cancellationToken);
	}

	public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindBySubjectAsync(
		string subject,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
			.Where(x => x.Subject == subject)
			.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			yield return authorization;
		}
	}

	public ValueTask<string?> GetApplicationIdAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(authorization.ApplicationId);
	}

	public ValueTask<TResult?> GetAsync<TState, TResult>(
		Func<IQueryable<OpenIddictAuthorizationDocument>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public ValueTask<DateTimeOffset?> GetCreationDateAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		return new ValueTask<DateTimeOffset?>(authorization.CreationDate);
	}

	public ValueTask<string?> GetIdAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(authorization.Id);
	}

	public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		var properties = authorization.Properties
			.ToImmutableDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
		return new ValueTask<ImmutableDictionary<string, JsonElement>>(properties);
	}

	public ValueTask<ImmutableArray<string>> GetScopesAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		return new ValueTask<ImmutableArray<string>>(authorization.Scopes.ToImmutableArray());
	}

	public ValueTask<string?> GetStatusAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(authorization.Status);
	}

	public ValueTask<string?> GetSubjectAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(authorization.Subject);
	}

	public ValueTask<string?> GetTypeAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(authorization.Type);
	}

	public ValueTask<OpenIddictAuthorizationDocument> InstantiateAsync(CancellationToken cancellationToken)
	{
		return new ValueTask<OpenIddictAuthorizationDocument>(new OpenIddictAuthorizationDocument());
	}

	public async IAsyncEnumerable<OpenIddictAuthorizationDocument> ListAsync(
		int? count,
		int? offset,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictAuthorizationDocument>().OrderBy(x => x.Id);

		if (offset.HasValue)
		{
			query = (IOrderedQueryable<OpenIddictAuthorizationDocument>)query.Skip(offset.Value);
		}

		if (count.HasValue)
		{
			query = (IOrderedQueryable<OpenIddictAuthorizationDocument>)query.Take(count.Value);
		}

		var authorizations = await query.ToListAsync(cancellationToken);
		foreach (var authorization in authorizations)
		{
			yield return authorization;
		}
	}

	public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
		Func<IQueryable<OpenIddictAuthorizationDocument>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
			.Where(x => x.CreationDate < threshold &&
				(x.Status == OpenIddictConstants.Statuses.Inactive ||
				 x.Status == OpenIddictConstants.Statuses.Revoked))
			.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			session.Delete(authorization);
		}

		await session.SaveChangesAsync(cancellationToken);
		return authorizations.Count;
	}

	public async ValueTask<long> RevokeAsync(
		string? subject,
		string? client,
		string? status,
		string? type,
		CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

		if (!string.IsNullOrEmpty(subject))
		{
			query = query.Where(x => x.Subject == subject);
		}

		if (!string.IsNullOrEmpty(client))
		{
			query = query.Where(x => x.ApplicationId == client);
		}

		if (!string.IsNullOrEmpty(status))
		{
			query = query.Where(x => x.Status == status);
		}

		if (!string.IsNullOrEmpty(type))
		{
			query = query.Where(x => x.Type == type);
		}

		var authorizations = await query.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			authorization.Status = OpenIddictConstants.Statuses.Revoked;
			session.Store(authorization);
		}

		await session.SaveChangesAsync(cancellationToken);
		return authorizations.Count;
	}

	public async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
			.Where(x => x.ApplicationId == identifier)
			.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			authorization.Status = OpenIddictConstants.Statuses.Revoked;
			session.Store(authorization);
		}

		await session.SaveChangesAsync(cancellationToken);
		return authorizations.Count;
	}

	public async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
			.Where(x => x.Subject == subject)
			.ToListAsync(cancellationToken);

		foreach (var authorization in authorizations)
		{
			authorization.Status = OpenIddictConstants.Statuses.Revoked;
			session.Store(authorization);
		}

		await session.SaveChangesAsync(cancellationToken);
		return authorizations.Count;
	}

	public ValueTask SetApplicationIdAsync(
		OpenIddictAuthorizationDocument authorization,
		string? identifier,
		CancellationToken cancellationToken)
	{
		authorization.ApplicationId = identifier;
		return default;
	}

	public ValueTask SetCreationDateAsync(
		OpenIddictAuthorizationDocument authorization,
		DateTimeOffset? date,
		CancellationToken cancellationToken)
	{
		authorization.CreationDate = date;
		return default;
	}

	public ValueTask SetPropertiesAsync(
		OpenIddictAuthorizationDocument authorization,
		ImmutableDictionary<string, JsonElement> properties,
		CancellationToken cancellationToken)
	{
		authorization.Properties = properties.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
		return default;
	}

	public ValueTask SetScopesAsync(
		OpenIddictAuthorizationDocument authorization,
		ImmutableArray<string> scopes,
		CancellationToken cancellationToken)
	{
		authorization.Scopes = scopes.ToHashSet();
		return default;
	}

	public ValueTask SetStatusAsync(
		OpenIddictAuthorizationDocument authorization,
		string? status,
		CancellationToken cancellationToken)
	{
		authorization.Status = status;
		return default;
	}

	public ValueTask SetSubjectAsync(
		OpenIddictAuthorizationDocument authorization,
		string? subject,
		CancellationToken cancellationToken)
	{
		authorization.Subject = subject;
		return default;
	}

	public ValueTask SetTypeAsync(
		OpenIddictAuthorizationDocument authorization,
		string? type,
		CancellationToken cancellationToken)
	{
		authorization.Type = type;
		return default;
	}

	public async ValueTask UpdateAsync(
		OpenIddictAuthorizationDocument authorization,
		CancellationToken cancellationToken)
	{
		authorization.ConcurrencyToken = Guid.NewGuid().ToString();
		await using var session = _store.LightweightSession();
		session.Store(authorization);
		await session.SaveChangesAsync(cancellationToken);
	}
}
