using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Infrastructure.Persistence;
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
	private readonly ITenantSessionFactory _sessionFactory;

	public MartenScopeStore(ITenantSessionFactory sessionFactory)
	{
		_sessionFactory = sessionFactory;
	}

	public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
	{
		await using var session = _sessionFactory.OpenQuerySession();
		return await session.Query<OAuthScopeState>()
			.Where(x => !x.IsDeleted)
			.CountAsync(cancellationToken);
	}

	public async ValueTask<long> CountAsync<TResult>(
		Func<IQueryable<OAuthScopeState>, IQueryable<TResult>> query,
		CancellationToken cancellationToken)
	{
		await using var session = _sessionFactory.OpenQuerySession();
		return query(session.Query<OAuthScopeState>().Where(x => !x.IsDeleted)).LongCount();
	}

	public async ValueTask CreateAsync(OAuthScopeState scope, CancellationToken cancellationToken)
	{
		await using var session = _sessionFactory.OpenSession();

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

		// Extract identity resource properties from the Properties dictionary and emit individual events
		ExtractAndApplyIdentityResourceProperties(scope);

		if (!scope.Enabled)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopeEnabledChanged(scope.Id, scope.Enabled));
		}

		if (scope.Required)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopeRequiredChanged(scope.Id, scope.Required));
		}

		if (scope.Emphasize)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopeEmphasizeChanged(scope.Id, scope.Emphasize));
		}

		if (!scope.ShowInDiscoveryDocument)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopeShowInDiscoveryDocumentChanged(scope.Id, scope.ShowInDiscoveryDocument));
		}

		if (scope.UserClaims.Count > 0)
		{
			session.Events.Append(scope.Id, new Domain.Events.OAuthScopeUserClaimsChanged(scope.Id, scope.UserClaims));
		}

		await session.SaveChangesAsync(cancellationToken);
	}

	public async ValueTask DeleteAsync(OAuthScopeState scope, CancellationToken cancellationToken)
	{
		await using var session = _sessionFactory.OpenSession();

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

		await using var session = _sessionFactory.OpenQuerySession();
		var state = await session.LoadAsync<OAuthScopeState>(id, cancellationToken);
		return state?.IsDeleted == true ? null : state;
	}

	public async ValueTask<OAuthScopeState?> FindByNameAsync(
		string name,
		CancellationToken cancellationToken)
	{
		await using var session = _sessionFactory.OpenQuerySession();
		return await session.Query<OAuthScopeState>()
			.FirstOrDefaultAsync(x => x.Name == name && !x.IsDeleted, cancellationToken);
	}

	public async IAsyncEnumerable<OAuthScopeState> FindByNamesAsync(
		ImmutableArray<string> names,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _sessionFactory.OpenQuerySession();
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
		await using var session = _sessionFactory.OpenQuerySession();
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
		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>();

		// Include generic properties
		foreach (var kvp in scope.Properties)
		{
			builder[kvp.Key] = JsonSerializer.SerializeToElement(kvp.Value);
		}

		// Include identity resource properties so the service layer can read them
		builder[ScopePropertyKeys.Enabled] = JsonSerializer.SerializeToElement(scope.Enabled);
		builder[ScopePropertyKeys.Required] = JsonSerializer.SerializeToElement(scope.Required);
		builder[ScopePropertyKeys.Emphasize] = JsonSerializer.SerializeToElement(scope.Emphasize);
		builder[ScopePropertyKeys.ShowInDiscoveryDocument] = JsonSerializer.SerializeToElement(scope.ShowInDiscoveryDocument);
		builder[ScopePropertyKeys.UserClaims] = JsonSerializer.SerializeToElement(scope.UserClaims);

		return new ValueTask<ImmutableDictionary<string, JsonElement>>(builder.ToImmutable());
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
		await using var session = _sessionFactory.OpenQuerySession();
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
		await using var session = _sessionFactory.OpenSession();

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

		// Extract identity resource properties from Properties dictionary before comparing
		ExtractAndApplyIdentityResourceProperties(scope);

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

		if (scope.Enabled != currentState.Enabled)
		{
			var evt = aggregate.SetEnabled(scope.Enabled);
			session.Events.Append(scope.Id, evt);
		}

		if (scope.Required != currentState.Required)
		{
			var evt = aggregate.SetRequired(scope.Required);
			session.Events.Append(scope.Id, evt);
		}

		if (scope.Emphasize != currentState.Emphasize)
		{
			var evt = aggregate.SetEmphasize(scope.Emphasize);
			session.Events.Append(scope.Id, evt);
		}

		if (scope.ShowInDiscoveryDocument != currentState.ShowInDiscoveryDocument)
		{
			var evt = aggregate.SetShowInDiscoveryDocument(scope.ShowInDiscoveryDocument);
			session.Events.Append(scope.Id, evt);
		}

		if (!scope.UserClaims.SequenceEqual(currentState.UserClaims))
		{
			var evt = aggregate.SetUserClaims(scope.UserClaims);
			session.Events.Append(scope.Id, evt);
		}

		await session.SaveChangesAsync(cancellationToken);
	}

	/// <summary>
	/// Extracts identity resource properties from the Properties dictionary
	/// into first-class fields on the scope state, then removes them from Properties
	/// so they are stored as individual events rather than in the generic Properties bag.
	/// </summary>
	private static void ExtractAndApplyIdentityResourceProperties(OAuthScopeState scope)
	{
		if (TryGetJsonProperty(scope.Properties, ScopePropertyKeys.Enabled, out var enabledEl)
		    && enabledEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			scope.Enabled = enabledEl.GetBoolean();
			scope.Properties.Remove(ScopePropertyKeys.Enabled);
		}

		if (TryGetJsonProperty(scope.Properties, ScopePropertyKeys.Required, out var requiredEl)
		    && requiredEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			scope.Required = requiredEl.GetBoolean();
			scope.Properties.Remove(ScopePropertyKeys.Required);
		}

		if (TryGetJsonProperty(scope.Properties, ScopePropertyKeys.Emphasize, out var emphasizeEl)
		    && emphasizeEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			scope.Emphasize = emphasizeEl.GetBoolean();
			scope.Properties.Remove(ScopePropertyKeys.Emphasize);
		}

		if (TryGetJsonProperty(scope.Properties, ScopePropertyKeys.ShowInDiscoveryDocument, out var showEl)
		    && showEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			scope.ShowInDiscoveryDocument = showEl.GetBoolean();
			scope.Properties.Remove(ScopePropertyKeys.ShowInDiscoveryDocument);
		}

		if (TryGetJsonProperty(scope.Properties, ScopePropertyKeys.UserClaims, out var claimsEl)
		    && claimsEl.ValueKind == JsonValueKind.Array)
		{
			scope.UserClaims = claimsEl.EnumerateArray()
				.Where(e => e.ValueKind == JsonValueKind.String)
				.Select(e => e.GetString()!)
				.ToList();
			scope.Properties.Remove(ScopePropertyKeys.UserClaims);
		}
	}

	/// <summary>
	/// Tries to get a JSON element from the Properties dictionary.
	/// Properties values may be stored as JsonElement or deserialized objects.
	/// </summary>
	private static bool TryGetJsonProperty(
		IDictionary<string, object?> properties,
		string key,
		out JsonElement element)
	{
		if (properties.TryGetValue(key, out var value))
		{
			if (value is JsonElement je)
			{
				element = je;
				return true;
			}

			// Value may have been deserialized to a primitive; re-serialize to JsonElement
			if (value is not null)
			{
				element = JsonSerializer.SerializeToElement(value);
				return true;
			}
		}

		element = default;
		return false;
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
