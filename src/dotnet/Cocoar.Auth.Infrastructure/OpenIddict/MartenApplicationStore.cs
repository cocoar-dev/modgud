using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
using Marten;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict application store using Marten event sourcing.
/// Uses hybrid storage:
/// - Event sourcing (OAuthApplicationAggregate → OAuthApplicationState) for non-sensitive data
/// - Document storage (OAuthApplicationSecurityData) for sensitive data (ClientSecret, JsonWebKeySet)
/// </summary>
public class MartenApplicationStore : IOpenIddictApplicationStore<OAuthApplicationState>
{
	private readonly IDocumentStore _store;

	public MartenApplicationStore(IDocumentStore store)
	{
		_store = store;
	}

	public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.Query<OAuthApplicationState>()
			.Where(x => !x.IsDeleted)
			.CountAsync(cancellationToken);
	}

	public async ValueTask<long> CountAsync<TResult>(
		Func<IQueryable<OAuthApplicationState>, IQueryable<TResult>> query,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return query(session.Query<OAuthApplicationState>().Where(x => !x.IsDeleted)).LongCount();
	}

	public async ValueTask CreateAsync(OAuthApplicationState application, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();

		// Create the aggregate and emit the creation event
		var (_, createdEvent) = OAuthApplicationAggregate.Create(
			application.Id,
			application.ClientId,
			application.DisplayName,
			application.ClientType,
			application.ConsentType,
			application.ApplicationType,
			application.RedirectUris,
			application.PostLogoutRedirectUris,
			application.Permissions,
			application.Requirements);

		// Start the event stream with the creation event
		session.Events.StartStream<OAuthApplicationAggregate>(application.Id, createdEvent);

		// Emit additional events for properties not covered in creation
		if (application.Settings.Count > 0)
		{
			session.Events.Append(application.Id, new Domain.Events.OAuthApplicationSettingsChanged(application.Id, application.Settings));
		}

		if (application.DisplayNames.Count > 0)
		{
			session.Events.Append(application.Id, new Domain.Events.OAuthApplicationDisplayNamesChanged(application.Id, application.DisplayNames));
		}

		if (application.Properties.Count > 0)
		{
			session.Events.Append(application.Id, new Domain.Events.OAuthApplicationPropertiesChanged(application.Id, application.Properties));
		}

		// Store security-sensitive data separately (not in event history)
		var securityData = OAuthApplicationSecurityData.Create(application.Id);

		// Apply pending security data from the application object
		if (application.PendingClientSecret is not null)
		{
			securityData.ClientSecret = application.PendingClientSecret;
		}
		if (application.PendingJsonWebKeySet is not null)
		{
			securityData.JsonWebKeySet = application.PendingJsonWebKeySet;
		}

		session.Store(securityData);

		await session.SaveChangesAsync(cancellationToken);
	}

	public async ValueTask DeleteAsync(OAuthApplicationState application, CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();

		// Load the aggregate to emit delete event
		var aggregate = await session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(application.Id, token: cancellationToken);
		if (aggregate is not null && !aggregate.IsDeleted)
		{
			var deletedEvent = aggregate.Delete();
			session.Events.Append(application.Id, deletedEvent);
		}

		// Also delete the security data
		session.Delete<OAuthApplicationSecurityData>(application.Id);

		await session.SaveChangesAsync(cancellationToken);
	}

	public async ValueTask<OAuthApplicationState?> FindByClientIdAsync(
		string identifier,
		CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		return await session.Query<OAuthApplicationState>()
			.FirstOrDefaultAsync(x => x.ClientId == identifier && !x.IsDeleted, cancellationToken);
	}

	public async ValueTask<OAuthApplicationState?> FindByIdAsync(
		string identifier,
		CancellationToken cancellationToken)
	{
		if (!Guid.TryParse(identifier, out var id))
		{
			return null;
		}

		await using var session = _store.QuerySession();
		var state = await session.LoadAsync<OAuthApplicationState>(id, cancellationToken);
		return state?.IsDeleted == true ? null : state;
	}

	public async IAsyncEnumerable<OAuthApplicationState> FindByPostLogoutRedirectUriAsync(
		string uri,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var applications = await session.Query<OAuthApplicationState>()
			.Where(x => x.PostLogoutRedirectUris.Contains(uri) && !x.IsDeleted)
			.ToListAsync(cancellationToken);

		foreach (var application in applications)
		{
			yield return application;
		}
	}

	public async IAsyncEnumerable<OAuthApplicationState> FindByRedirectUriAsync(
		string uri,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var applications = await session.Query<OAuthApplicationState>()
			.Where(x => x.RedirectUris.Contains(uri) && !x.IsDeleted)
			.ToListAsync(cancellationToken);

		foreach (var application in applications)
		{
			yield return application;
		}
	}

	public ValueTask<string?> GetApplicationTypeAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(application.ApplicationType);
	}

	public ValueTask<TResult?> GetAsync<TState, TResult>(
		Func<IQueryable<OAuthApplicationState>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public ValueTask<string?> GetClientIdAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(application.ClientId);
	}

	public async ValueTask<string?> GetClientSecretAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		// Check pending secret first (for validation during creation flow)
		if (application.PendingClientSecret is not null)
		{
			return application.PendingClientSecret;
		}

		// Client secret is stored in security data document, not in event-sourced state
		await using var session = _store.QuerySession();
		var securityData = await session.LoadAsync<OAuthApplicationSecurityData>(application.Id, cancellationToken);
		return securityData?.ClientSecret;
	}

	public ValueTask<string?> GetClientTypeAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(application.ClientType);
	}

	public ValueTask<string?> GetConsentTypeAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(application.ConsentType);
	}

	public ValueTask<string?> GetDisplayNameAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(application.DisplayName);
	}

	public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		var displayNames = application.DisplayNames
			.ToImmutableDictionary(x => CultureInfo.GetCultureInfo(x.Key), x => x.Value);
		return new ValueTask<ImmutableDictionary<CultureInfo, string>>(displayNames);
	}

	public ValueTask<string?> GetIdAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<string?>(application.Id.ToString());
	}

	public async ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		// JSON Web Key Set is stored in security data document, not in event-sourced state
		await using var session = _store.QuerySession();
		var securityData = await session.LoadAsync<OAuthApplicationSecurityData>(application.Id, cancellationToken);

		if (string.IsNullOrEmpty(securityData?.JsonWebKeySet))
		{
			return null;
		}

		return JsonWebKeySet.Create(securityData.JsonWebKeySet);
	}

	public ValueTask<ImmutableArray<string>> GetPermissionsAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<ImmutableArray<string>>(application.Permissions.ToImmutableArray());
	}

	public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<ImmutableArray<string>>(application.PostLogoutRedirectUris.ToImmutableArray());
	}

	public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		var properties = application.Properties
			.ToImmutableDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
		return new ValueTask<ImmutableDictionary<string, JsonElement>>(properties);
	}

	public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<ImmutableArray<string>>(application.RedirectUris.ToImmutableArray());
	}

	public ValueTask<ImmutableArray<string>> GetRequirementsAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<ImmutableArray<string>>(application.Requirements.ToImmutableArray());
	}

	public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		return new ValueTask<ImmutableDictionary<string, string>>(application.Settings.ToImmutableDictionary());
	}

	public ValueTask<OAuthApplicationState> InstantiateAsync(CancellationToken cancellationToken)
	{
		return new ValueTask<OAuthApplicationState>(new OAuthApplicationState
		{
			Id = Guid.NewGuid()
		});
	}

	public async IAsyncEnumerable<OAuthApplicationState> ListAsync(
		int? count,
		int? offset,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var session = _store.QuerySession();
		var query = session.Query<OAuthApplicationState>()
			.Where(x => !x.IsDeleted)
			.OrderBy(x => x.Id);

		if (offset.HasValue)
		{
			query = (IOrderedQueryable<OAuthApplicationState>)query.Skip(offset.Value);
		}

		if (count.HasValue)
		{
			query = (IOrderedQueryable<OAuthApplicationState>)query.Take(count.Value);
		}

		var applications = await query.ToListAsync(cancellationToken);
		foreach (var application in applications)
		{
			yield return application;
		}
	}

	public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
		Func<IQueryable<OAuthApplicationState>, TState, IQueryable<TResult>> query,
		TState state,
		CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public ValueTask SetApplicationTypeAsync(
		OAuthApplicationState application,
		string? type,
		CancellationToken cancellationToken)
	{
		application.ApplicationType = type;
		return default;
	}

	public ValueTask SetClientIdAsync(
		OAuthApplicationState application,
		string? identifier,
		CancellationToken cancellationToken)
	{
		application.ClientId = identifier ?? string.Empty;
		return default;
	}

	public ValueTask SetClientSecretAsync(
		OAuthApplicationState application,
		string? secret,
		CancellationToken cancellationToken)
	{
		// Store temporarily on the application object for validation
		// This will be persisted to OAuthApplicationSecurityData in CreateAsync/UpdateAsync
		application.PendingClientSecret = secret;
		return default;
	}

	public ValueTask SetClientTypeAsync(
		OAuthApplicationState application,
		string? type,
		CancellationToken cancellationToken)
	{
		application.ClientType = type;
		return default;
	}

	public ValueTask SetConsentTypeAsync(
		OAuthApplicationState application,
		string? type,
		CancellationToken cancellationToken)
	{
		application.ConsentType = type;
		return default;
	}

	public ValueTask SetDisplayNameAsync(
		OAuthApplicationState application,
		string? name,
		CancellationToken cancellationToken)
	{
		application.DisplayName = name;
		return default;
	}

	public ValueTask SetDisplayNamesAsync(
		OAuthApplicationState application,
		ImmutableDictionary<CultureInfo, string> names,
		CancellationToken cancellationToken)
	{
		application.DisplayNames = names.ToDictionary(x => x.Key.Name, x => x.Value);
		return default;
	}

	public ValueTask SetJsonWebKeySetAsync(
		OAuthApplicationState application,
		JsonWebKeySet? set,
		CancellationToken cancellationToken)
	{
		// Store temporarily on the application object for validation
		// This will be persisted to OAuthApplicationSecurityData in CreateAsync/UpdateAsync
		application.PendingJsonWebKeySet = set is null ? null : JsonSerializer.Serialize(set);
		return default;
	}

	public ValueTask SetPermissionsAsync(
		OAuthApplicationState application,
		ImmutableArray<string> permissions,
		CancellationToken cancellationToken)
	{
		application.Permissions = permissions.ToList();
		return default;
	}

	public ValueTask SetPostLogoutRedirectUrisAsync(
		OAuthApplicationState application,
		ImmutableArray<string> uris,
		CancellationToken cancellationToken)
	{
		application.PostLogoutRedirectUris = uris.ToList();
		return default;
	}

	public ValueTask SetPropertiesAsync(
		OAuthApplicationState application,
		ImmutableDictionary<string, JsonElement> properties,
		CancellationToken cancellationToken)
	{
		application.Properties = properties.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
		return default;
	}

	public ValueTask SetRedirectUrisAsync(
		OAuthApplicationState application,
		ImmutableArray<string> uris,
		CancellationToken cancellationToken)
	{
		application.RedirectUris = uris.ToList();
		return default;
	}

	public ValueTask SetRequirementsAsync(
		OAuthApplicationState application,
		ImmutableArray<string> requirements,
		CancellationToken cancellationToken)
	{
		application.Requirements = requirements.ToList();
		return default;
	}

	public ValueTask SetSettingsAsync(
		OAuthApplicationState application,
		ImmutableDictionary<string, string> settings,
		CancellationToken cancellationToken)
	{
		application.Settings = settings.ToDictionary(x => x.Key, x => x.Value);
		return default;
	}

	public async ValueTask UpdateAsync(
		OAuthApplicationState application,
		CancellationToken cancellationToken)
	{
		await using var session = _store.LightweightSession();

		// Load the current state from projection to compare changes
		var currentState = await session.LoadAsync<OAuthApplicationState>(application.Id, cancellationToken);
		if (currentState is null)
		{
			// This is a new application being created via the OpenIddict pattern
			// (InstantiateAsync -> Set* -> CreateAsync pathway already handled in CreateAsync)
			return;
		}

		// Load the aggregate to emit update events
		var aggregate = await session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(application.Id, token: cancellationToken);
		if (aggregate is null)
		{
			return;
		}

		// Emit events for changed properties
		if (application.DisplayName != currentState.DisplayName)
		{
			var evt = aggregate.SetDisplayName(application.DisplayName);
			session.Events.Append(application.Id, evt);
		}

		if (application.ClientType != currentState.ClientType)
		{
			var evt = aggregate.SetClientType(application.ClientType);
			session.Events.Append(application.Id, evt);
		}

		if (application.ConsentType != currentState.ConsentType)
		{
			var evt = aggregate.SetConsentType(application.ConsentType);
			session.Events.Append(application.Id, evt);
		}

		if (!application.RedirectUris.SequenceEqual(currentState.RedirectUris))
		{
			var evt = aggregate.SetRedirectUris(application.RedirectUris);
			session.Events.Append(application.Id, evt);
		}

		if (!application.PostLogoutRedirectUris.SequenceEqual(currentState.PostLogoutRedirectUris))
		{
			var evt = aggregate.SetPostLogoutRedirectUris(application.PostLogoutRedirectUris);
			session.Events.Append(application.Id, evt);
		}

		if (!application.Permissions.SequenceEqual(currentState.Permissions))
		{
			var evt = aggregate.SetPermissions(application.Permissions);
			session.Events.Append(application.Id, evt);
		}

		if (!application.Requirements.SequenceEqual(currentState.Requirements))
		{
			var evt = aggregate.SetRequirements(application.Requirements);
			session.Events.Append(application.Id, evt);
		}

		if (!DictionaryEquals(application.Settings, currentState.Settings))
		{
			var evt = aggregate.SetSettings(application.Settings);
			session.Events.Append(application.Id, evt);
		}

		if (!DictionaryEquals(application.DisplayNames, currentState.DisplayNames))
		{
			var evt = aggregate.SetDisplayNames(application.DisplayNames);
			session.Events.Append(application.Id, evt);
		}

		if (!DictionaryEquals(application.Properties, currentState.Properties))
		{
			var evt = aggregate.SetProperties(application.Properties);
			session.Events.Append(application.Id, evt);
		}

		// Handle security data updates (stored separately, not event-sourced)
		if (application.PendingClientSecret is not null || application.PendingJsonWebKeySet is not null)
		{
			var securityData = await session.LoadAsync<OAuthApplicationSecurityData>(application.Id, cancellationToken)
				?? OAuthApplicationSecurityData.Create(application.Id);

			if (application.PendingClientSecret is not null)
			{
				securityData.ClientSecret = application.PendingClientSecret;
			}

			if (application.PendingJsonWebKeySet is not null)
			{
				securityData.JsonWebKeySet = application.PendingJsonWebKeySet;
			}

			securityData.UpdateConcurrencyToken();
			session.Store(securityData);
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
