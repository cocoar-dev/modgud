using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Marten;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict scope store using Marten event sourcing.
/// Scopes don't contain sensitive data, so everything is event-sourced.
/// </summary>
public class MartenScopeStore : IOpenIddictScopeStore<OAuthScopeState>
{
	private readonly IDocumentStore _store;

	public MartenScopeStore(IDocumentStore store)
	{
		_store = store;
	}

	public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.Query<OAuthScopeState>()
			.Where(x => !x.IsDeleted)
			.CountAsync(cancellationToken);
	}

	public async ValueTask<long> CountAsync<TResult>(
		Func<IQueryable<OAuthScopeState>, IQueryable<TResult>> query,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return query(session.Query<OAuthScopeState>().Where(x => !x.IsDeleted)).LongCount();
	}

	public async ValueTask CreateAsync(OAuthScopeState scope, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();

		// Create the aggregate and emit the creation event
		var (_, createdEvent) = OAuthScopeAggregate.Create(
			scope.Id,
			scope.Name,
			scope.DisplayName,
			scope.Description,
			scope.Resources);

		// Start the event stream with the creation event
		session.Events.StartStream<OAuthScopeAggregate>(scope.Id, createdEvent);

		// Emit additional events for properties not covered in creation
		if (scope.DisplayNames.Count > 0)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopeDisplayNamesChanged(scope.Id, scope.DisplayNames));
		}

		if (scope.Descriptions.Count > 0)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopeDescriptionsChanged(scope.Id, scope.Descriptions));
		}

		if (scope.Properties.Count > 0)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopePropertiesChanged(scope.Id, scope.Properties));
		}

		await session.SaveChangesAsync(cancellationToken);
	}

	public async ValueTask DeleteAsync(OAuthScopeState scope, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();

		// Load the aggregate to emit delete event
		var aggregate = await session.Events.AggregateStreamAsync<OAuthScopeAggregate>(scope.Id, token: cancellationToken);
		if (aggregate is not null && !aggregate.IsDeleted)
		{
			var deletedEvent = aggregate.Delete();
			session.Events.Append(scope.Id, deletedEvent);
		}

		await session.SaveChangesAsync(cancellationToken);
	}

	public async ValueTask<OAuthScopeState?> FindByIdAsync(
		string identifier,
		CancellationToken cancellationToken)
	{
		if (!Guid.TryParse(identifier, out var id))
		{
			return null;
		}

		await using var session = _store.QuerySession();
		var state = await session.LoadAsync<OAuthScopeState>(id, cancellationToken);
		return state?.IsDeleted == true ? null : state;
	}

	public async ValueTask<OAuthScopeState?> FindByNameAsync(
		string name,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.Query<OAuthScopeState>()
			.FirstOrDefaultAsync(x => x.Name == name && !x.IsDeleted, cancellationToken);
	}

	public async IAsyncEnumerable<OAuthScopeState> FindByNamesAsync(
		ImmutableArray<string> names,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var scopes = await session.Query<OAuthScopeState>()
			.Where(x => x.Name != null && names.Contains(x.Name) && !x.IsDeleted)
			.ToListAsync(cancellationToken);

		foreach (var scope in scopes)
		{
			yield return scope;
		}
	}

	public async IAsyncEnumerable<OAuthScopeState> FindByResourceAsync(
		string resource,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var scopes = await session.Query<OAuthScopeState>()
			.Where(x => x.Resources.Contains(resource) && !x.IsDeleted)
			.ToListAsync(cancellationToken);

		foreach (var scope in scopes)
		{
			yield return scope;
		}
	}

	public ValueTask<TResult?> GetAsync<TState, TResult>(
		Func<IQueryable<OAuthScopeState>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public ValueTask<string?> GetDescriptionAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(scope.Description);
	}

	public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDescriptionsAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		var descriptions = scope.Descriptions
			.ToImmutableDictionary(x => CultureInfo.GetCultureInfo(x.Key), x => x.Value);
		return new ValueTask<ImmutableDictionary<CultureInfo, string>>(descriptions);
	}

	public ValueTask<string?> GetDisplayNameAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(scope.DisplayName);
	}

	public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		var displayNames = scope.DisplayNames
			.ToImmutableDictionary(x => CultureInfo.GetCultureInfo(x.Key), x => x.Value);
		return new ValueTask<ImmutableDictionary<CultureInfo, string>>(displayNames);
	}

	public ValueTask<string?> GetIdAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(scope.Id.ToString());
	}

	public ValueTask<string?> GetNameAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(scope.Name);
	}

	public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		var properties = scope.Properties
			.ToImmutableDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
		return new ValueTask<ImmutableDictionary<string, JsonElement>>(properties);
	}

	public ValueTask<ImmutableArray<string>> GetResourcesAsync(
		OAuthScopeState scope,
		CancellationToken cancellationToken)
	{
		return new ValueTask<ImmutableArray<string>>(scope.Resources.ToImmutableArray());
	}

	public ValueTask<OAuthScopeState> InstantiateAsync(CancellationToken cancellationToken)
	{
		return new ValueTask<OAuthScopeState>(new OAuthScopeState
		{
			Id = Guid.NewGuid()
		});
	}

	public async IAsyncEnumerable<OAuthScopeState> ListAsync(
		int? count,
		int? offset,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OAuthScopeState>()
			.Where(x => !x.IsDeleted)
			.OrderBy(x => x.Id);

		if (offset.HasValue)
		{
			query = (IOrderedQueryable<OAuthScopeState>)query.Skip(offset.Value);
		}

		if (count.HasValue)
		{
			query = (IOrderedQueryable<OAuthScopeState>)query.Take(count.Value);
		}

		var scopes = await query.ToListAsync(cancellationToken);
		foreach (var scope in scopes)
		{
			yield return scope;
		}
	}

	public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
		Func<IQueryable<OAuthScopeState>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public ValueTask SetDescriptionAsync(
		OAuthScopeState scope,
		string? description,
		CancellationToken cancellationToken)
	{
		scope.Description = description;
		return default;
	}

	public ValueTask SetDescriptionsAsync(
		OAuthScopeState scope,
		ImmutableDictionary<CultureInfo, string> descriptions,
		CancellationToken cancellationToken)
	{
		scope.Descriptions = descriptions.ToDictionary(x => x.Key.Name, x => x.Value);
		return default;
	}

	public ValueTask SetDisplayNameAsync(
		OAuthScopeState scope,
		string? name,
		CancellationToken cancellationToken)
	{
		scope.DisplayName = name;
		return default;
	}

	public ValueTask SetDisplayNamesAsync(
		OAuthScopeState scope,
		ImmutableDictionary<CultureInfo, string> names,
		CancellationToken cancellationToken)
	{
		scope.DisplayNames = names.ToDictionary(x => x.Key.Name, x => x.Value);
		return default;
	}

	public ValueTask SetNameAsync(
		OAuthScopeState scope,
		string? name,
		CancellationToken cancellationToken)
	{
		scope.Name = name ?? string.Empty;
		return default;
	}

	public ValueTask SetPropertiesAsync(
		OAuthScopeState scope,
		ImmutableDictionary<string, JsonElement> properties,
		CancellationToken cancellationToken)
	{
		scope.Properties = properties.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
		return default;
	}

	public ValueTask SetResourcesAsync(
		OAuthScopeState scope,
		ImmutableArray<string> resources,
		CancellationToken cancellationToken)
	{
		scope.Resources = resources.ToList();
		return default;
	}

	public async ValueTask UpdateAsync(OAuthScopeState scope, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();

		// Load the current state from projection to compare changes
		var currentState = await session.LoadAsync<OAuthScopeState>(scope.Id, cancellationToken);
		if (currentState is null)
		{
			// This is a new scope being created via the OpenIddict pattern
			// (InstantiateAsync -> Set* -> CreateAsync pathway already handled in CreateAsync)
			return;
		}

		// Load the aggregate to emit update events
		var aggregate = await session.Events.AggregateStreamAsync<OAuthScopeAggregate>(scope.Id, token: cancellationToken);
		if (aggregate is null)
		{
			return;
		}

		// Emit events for changed properties
		if (scope.DisplayName != currentState.DisplayName)
		{
			var evt = aggregate.SetDisplayName(scope.DisplayName);
			session.Events.Append(scope.Id, evt);
		}

		if (scope.Description != currentState.Description)
		{
			var evt = aggregate.SetDescription(scope.Description);
			session.Events.Append(scope.Id, evt);
		}

		if (!scope.Resources.SequenceEqual(currentState.Resources))
		{
			var evt = aggregate.SetResources(scope.Resources);
			session.Events.Append(scope.Id, evt);
		}

		if (!DictionaryEquals(scope.DisplayNames, currentState.DisplayNames))
		{
			var evt = aggregate.SetDisplayNames(scope.DisplayNames);
			session.Events.Append(scope.Id, evt);
		}

		if (!DictionaryEquals(scope.Descriptions, currentState.Descriptions))
		{
			var evt = aggregate.SetDescriptions(scope.Descriptions);
			session.Events.Append(scope.Id, evt);
		}

		if (!DictionaryEquals(scope.Properties, currentState.Properties))
		{
			var evt = aggregate.SetProperties(scope.Properties);
			session.Events.Append(scope.Id, evt);
		}

		await session.SaveChangesAsync(cancellationToken);
	}

	private static bool DictionaryEquals<TKey, TValue>(IDictionary<TKey, TValue> a, IDictionary<TKey, TValue> b)
		where TKey : notnull
	{
		if (a.Count != b.Count)
			return false;

		foreach (var kvp in a)
		{
			if (!b.TryGetValue(kvp.Key, out var value) || !Equals(kvp.Value, value))
				return false;
		}

		return true;
	}
}
