using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cocoar.Auth.Domain.Entities;
using Marten;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict token store using Marten document storage.
/// </summary>
public class MartenTokenStore : IOpenIddictTokenStore<OpenIddictTokenDocument>
{
	private readonly IDocumentStore _store;

	public MartenTokenStore(IDocumentStore store)
	{
		_store = store;
	}

	public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.Query<OpenIddictTokenDocument>().CountAsync(cancellationToken);
	}

	public async ValueTask<long> CountAsync<TResult>(
		Func<IQueryable<OpenIddictTokenDocument>, IQueryable<TResult>> query,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return query(session.Query<OpenIddictTokenDocument>()).LongCount();
	}

	public async ValueTask CreateAsync(OpenIddictTokenDocument token, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		session.Store(token);
		await session.SaveChangesAsync(cancellationToken);
	}

	public async ValueTask DeleteAsync(OpenIddictTokenDocument token, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		session.Delete(token);
		await session.SaveChangesAsync(cancellationToken);
	}

	public async IAsyncEnumerable<OpenIddictTokenDocument> FindAsync(
		string? subject,
		string? client,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictTokenDocument>().AsQueryable();

		if (!string.IsNullOrEmpty(subject))
		{
			query = query.Where(x => x.Subject == subject);
		}

		if (!string.IsNullOrEmpty(client))
		{
			query = query.Where(x => x.ApplicationId == client);
		}

		var tokens = await query.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			yield return token;
		}
	}

	public async IAsyncEnumerable<OpenIddictTokenDocument> FindAsync(
		string? subject,
		string? client,
		string? status,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictTokenDocument>().AsQueryable();

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

		var tokens = await query.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			yield return token;
		}
	}

	public async IAsyncEnumerable<OpenIddictTokenDocument> FindAsync(
		string? subject,
		string? client,
		string? status,
		string? type,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictTokenDocument>().AsQueryable();

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

		var tokens = await query.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			yield return token;
		}
	}

	public async IAsyncEnumerable<OpenIddictTokenDocument> FindByApplicationIdAsync(
		string identifier,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var tokens = await session.Query<OpenIddictTokenDocument>()
			.Where(x => x.ApplicationId == identifier)
			.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			yield return token;
		}
	}

	public async IAsyncEnumerable<OpenIddictTokenDocument> FindByAuthorizationIdAsync(
		string identifier,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var tokens = await session.Query<OpenIddictTokenDocument>()
			.Where(x => x.AuthorizationId == identifier)
			.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			yield return token;
		}
	}

	public async ValueTask<OpenIddictTokenDocument?> FindByIdAsync(
		string identifier,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.LoadAsync<OpenIddictTokenDocument>(identifier, cancellationToken);
	}

	public async ValueTask<OpenIddictTokenDocument?> FindByReferenceIdAsync(
		string identifier,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.Query<OpenIddictTokenDocument>()
			.FirstOrDefaultAsync(x => x.ReferenceId == identifier, cancellationToken);
	}

	public async IAsyncEnumerable<OpenIddictTokenDocument> FindBySubjectAsync(
		string subject,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var tokens = await session.Query<OpenIddictTokenDocument>()
			.Where(x => x.Subject == subject)
			.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			yield return token;
		}
	}

	public ValueTask<string?> GetApplicationIdAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.ApplicationId);
	}

	public ValueTask<TResult?> GetAsync<TState, TResult>(
		Func<IQueryable<OpenIddictTokenDocument>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public ValueTask<string?> GetAuthorizationIdAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.AuthorizationId);
	}

	public ValueTask<DateTimeOffset?> GetCreationDateAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<DateTimeOffset?>(token.CreationDate);
	}

	public ValueTask<DateTimeOffset?> GetExpirationDateAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<DateTimeOffset?>(token.ExpirationDate);
	}

	public ValueTask<string?> GetIdAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.Id);
	}

	public ValueTask<string?> GetPayloadAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.Payload);
	}

	public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		var properties = token.Properties
			.ToImmutableDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
		return new ValueTask<ImmutableDictionary<string, JsonElement>>(properties);
	}

	public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<DateTimeOffset?>(token.RedemptionDate);
	}

	public ValueTask<string?> GetReferenceIdAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.ReferenceId);
	}

	public ValueTask<string?> GetStatusAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.Status);
	}

	public ValueTask<string?> GetSubjectAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.Subject);
	}

	public ValueTask<string?> GetTypeAsync(
		OpenIddictTokenDocument token,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(token.Type);
	}

	public ValueTask<OpenIddictTokenDocument> InstantiateAsync(CancellationToken cancellationToken)
	{
		return new ValueTask<OpenIddictTokenDocument>(new OpenIddictTokenDocument());
	}

	public async IAsyncEnumerable<OpenIddictTokenDocument> ListAsync(
		int? count,
		int? offset,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OpenIddictTokenDocument>().OrderBy(x => x.Id);

		if (offset.HasValue)
		{
			query = (IOrderedQueryable<OpenIddictTokenDocument>)query.Skip(offset.Value);
		}

		if (count.HasValue)
		{
			query = (IOrderedQueryable<OpenIddictTokenDocument>)query.Take(count.Value);
		}

		var tokens = await query.ToListAsync(cancellationToken);
		foreach (var token in tokens)
		{
			yield return token;
		}
	}

	public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
		Func<IQueryable<OpenIddictTokenDocument>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var tokens = await session.Query<OpenIddictTokenDocument>()
			.Where(x => x.CreationDate < threshold &&
				(x.Status == OpenIddictConstants.Statuses.Inactive ||
				 x.Status == OpenIddictConstants.Statuses.Revoked ||
				 x.ExpirationDate < DateTimeOffset.UtcNow))
			.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			session.Delete(token);
		}

		await session.SaveChangesAsync(cancellationToken);
		return tokens.Count;
	}

	public async ValueTask<long> RevokeAsync(
		string? subject,
		string? client,
		string? status,
		string? type,
		CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var query = session.Query<OpenIddictTokenDocument>().AsQueryable();

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

		var tokens = await query.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			token.Status = OpenIddictConstants.Statuses.Revoked;
			session.Store(token);
		}

		await session.SaveChangesAsync(cancellationToken);
		return tokens.Count;
	}

	public async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var tokens = await session.Query<OpenIddictTokenDocument>()
			.Where(x => x.ApplicationId == identifier)
			.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			token.Status = OpenIddictConstants.Statuses.Revoked;
			session.Store(token);
		}

		await session.SaveChangesAsync(cancellationToken);
		return tokens.Count;
	}

	public async ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var tokens = await session.Query<OpenIddictTokenDocument>()
			.Where(x => x.AuthorizationId == identifier)
			.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			token.Status = OpenIddictConstants.Statuses.Revoked;
			session.Store(token);
		}

		await session.SaveChangesAsync(cancellationToken);
		return tokens.Count;
	}

	public async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();
		var tokens = await session.Query<OpenIddictTokenDocument>()
			.Where(x => x.Subject == subject)
			.ToListAsync(cancellationToken);

		foreach (var token in tokens)
		{
			token.Status = OpenIddictConstants.Statuses.Revoked;
			session.Store(token);
		}

		await session.SaveChangesAsync(cancellationToken);
		return tokens.Count;
	}

	public ValueTask SetApplicationIdAsync(
		OpenIddictTokenDocument token,
		string? identifier,
		CancellationToken cancellationToken)
	{
		token.ApplicationId = identifier;
		return default;
	}

	public ValueTask SetAuthorizationIdAsync(
		OpenIddictTokenDocument token,
		string? identifier,
		CancellationToken cancellationToken)
	{
		token.AuthorizationId = identifier;
		return default;
	}

	public ValueTask SetCreationDateAsync(
		OpenIddictTokenDocument token,
		DateTimeOffset? date,
		CancellationToken cancellationToken)
	{
		token.CreationDate = date;
		return default;
	}

	public ValueTask SetExpirationDateAsync(
		OpenIddictTokenDocument token,
		DateTimeOffset? date,
		CancellationToken cancellationToken)
	{
		token.ExpirationDate = date;
		return default;
	}

	public ValueTask SetPayloadAsync(
		OpenIddictTokenDocument token,
		string? payload,
		CancellationToken cancellationToken)
	{
		token.Payload = payload;
		return default;
	}

	public ValueTask SetPropertiesAsync(
		OpenIddictTokenDocument token,
		ImmutableDictionary<string, JsonElement> properties,
		CancellationToken cancellationToken)
	{
		token.Properties = properties.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
		return default;
	}

	public ValueTask SetRedemptionDateAsync(
		OpenIddictTokenDocument token,
		DateTimeOffset? date,
		CancellationToken cancellationToken)
	{
		token.RedemptionDate = date;
		return default;
	}

	public ValueTask SetReferenceIdAsync(
		OpenIddictTokenDocument token,
		string? identifier,
		CancellationToken cancellationToken)
	{
		token.ReferenceId = identifier;
		return default;
	}

	public ValueTask SetStatusAsync(
		OpenIddictTokenDocument token,
		string? status,
		CancellationToken cancellationToken)
	{
		token.Status = status;
		return default;
	}

	public ValueTask SetSubjectAsync(
		OpenIddictTokenDocument token,
		string? subject,
		CancellationToken cancellationToken)
	{
		token.Subject = subject;
		return default;
	}

	public ValueTask SetTypeAsync(
		OpenIddictTokenDocument token,
		string? type,
		CancellationToken cancellationToken)
	{
		token.Type = type;
		return default;
	}

	public async ValueTask UpdateAsync(OpenIddictTokenDocument token, CancellationToken cancellationToken)
	{
		token.ConcurrencyToken = Guid.NewGuid().ToString();
		await using var session = _store.LightweightSession();
		session.Store(token);
		await session.SaveChangesAsync(cancellationToken);
	}
}
