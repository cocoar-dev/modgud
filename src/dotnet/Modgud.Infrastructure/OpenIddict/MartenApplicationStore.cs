using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict application store using Marten event sourcing.
/// Hybrid storage:
/// - Event sourcing (<see cref="OAuthApplicationAggregate"/> → <see cref="OAuthApplicationState"/>) for non-sensitive data
/// - Document storage (<see cref="OAuthApplicationSecurityData"/>) for ClientSecret + JsonWebKeySet
/// Sessions are tenant-scoped via <see cref="ITenantSessionFactory"/> so all reads/writes
/// land in the active realm's DB.
/// </summary>
public class MartenApplicationStore : IOpenIddictApplicationStore<OAuthApplicationState>
{
    private readonly ITenantSessionFactory _sessionFactory;
    private readonly Cimd.CimdClientResolver _cimdResolver;

    public MartenApplicationStore(ITenantSessionFactory sessionFactory, Cimd.CimdClientResolver cimdResolver)
    {
        _sessionFactory = sessionFactory;
        _cimdResolver = cimdResolver;
    }

    public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return await session.Query<OAuthApplicationState>()
            .Where(x => !x.IsDeleted)
            .CountAsync(cancellationToken);
    }

    public async ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OAuthApplicationState>, IQueryable<TResult>> query,
        CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return query(session.Query<OAuthApplicationState>().Where(x => !x.IsDeleted)).LongCount();
    }

    public async ValueTask CreateAsync(OAuthApplicationState application, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();

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

        session.Events.StartStream<OAuthApplicationAggregate>(application.Id, createdEvent);

        if (application.Settings.Count > 0)
            session.Events.Append(application.Id, new OAuthApplicationSettingsChanged(application.Id, application.Settings));

        if (application.DisplayNames.Count > 0)
            session.Events.Append(application.Id, new OAuthApplicationDisplayNamesChanged(application.Id, application.DisplayNames));

        if (application.Properties.Count > 0)
            session.Events.Append(application.Id, new OAuthApplicationPropertiesChanged(application.Id, application.Properties));

        // Security data lives outside the event stream so the hash never enters the audit log
        var securityData = OAuthApplicationSecurityData.Create(application.Id);
        if (application.PendingClientSecret is not null) securityData.ClientSecret = application.PendingClientSecret;
        if (application.PendingJsonWebKeySet is not null) securityData.JsonWebKeySet = application.PendingJsonWebKeySet;
        session.Store(securityData);

        await session.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DeleteAsync(OAuthApplicationState application, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();

        var aggregate = await session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(application.Id, token: cancellationToken);
        if (aggregate is not null && !aggregate.IsDeleted)
        {
            var deletedEvent = aggregate.Delete();
            session.Events.Append(application.Id, deletedEvent);
        }

        session.Delete<OAuthApplicationSecurityData>(application.Id);

        await session.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<OAuthApplicationState?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var stored = await session.Query<OAuthApplicationState>()
            .FirstOrDefaultAsync(x => x.ClientId == identifier && !x.IsDeleted, cancellationToken);
        if (stored is not null) return stored;

        // CIMD: a stored client always wins; only when the
        // identifier is unknown AND looks like a CIMD client_id URL (and the
        // realm has opted in) do we fetch + validate its metadata document
        // and return a synthesized, non-persisted public client. Returns null
        // for non-CIMD identifiers, disabled realms, or invalid documents.
        return await _cimdResolver.ResolveAsync(identifier, cancellationToken);
    }

    public async ValueTask<OAuthApplicationState?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(identifier, out var id)) return null;
        await using var session = _sessionFactory.OpenQuerySession();
        var state = await session.LoadAsync<OAuthApplicationState>(id, cancellationToken);
        return state?.IsDeleted == true ? null : state;
    }

    public async IAsyncEnumerable<OAuthApplicationState> FindByPostLogoutRedirectUriAsync(
        string uri,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var applications = await session.Query<OAuthApplicationState>()
            .Where(x => x.PostLogoutRedirectUris.Contains(uri) && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var application in applications) yield return application;
    }

    public async IAsyncEnumerable<OAuthApplicationState> FindByRedirectUriAsync(
        string uri,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var applications = await session.Query<OAuthApplicationState>()
            .Where(x => x.RedirectUris.Contains(uri) && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var application in applications) yield return application;
    }

    public ValueTask<string?> GetApplicationTypeAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.ApplicationType);

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OAuthApplicationState>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<string?> GetClientIdAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.ClientId);

    public async ValueTask<string?> GetClientSecretAsync(OAuthApplicationState application, CancellationToken cancellationToken)
    {
        if (application.PendingClientSecret is not null) return application.PendingClientSecret;
        await using var session = _sessionFactory.OpenQuerySession();
        var securityData = await session.LoadAsync<OAuthApplicationSecurityData>(application.Id, cancellationToken);
        return securityData?.ClientSecret;
    }

    public ValueTask<string?> GetClientTypeAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.ClientType);

    public ValueTask<string?> GetConsentTypeAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.ConsentType);

    public ValueTask<string?> GetDisplayNameAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.DisplayName);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OAuthApplicationState application, CancellationToken _)
    {
        var displayNames = application.DisplayNames
            .ToImmutableDictionary(x => CultureInfo.GetCultureInfo(x.Key), x => x.Value);
        return new ValueTask<ImmutableDictionary<CultureInfo, string>>(displayNames);
    }

    public ValueTask<string?> GetIdAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.Id.ToString());

    public async ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(OAuthApplicationState application, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var securityData = await session.LoadAsync<OAuthApplicationSecurityData>(application.Id, cancellationToken);
        if (string.IsNullOrEmpty(securityData?.JsonWebKeySet)) return null;
        return JsonWebKeySet.Create(securityData.JsonWebKeySet);
    }

    public ValueTask<ImmutableArray<string>> GetPermissionsAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.Permissions.ToImmutableArray());

    public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.PostLogoutRedirectUris.ToImmutableArray());

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OAuthApplicationState application, CancellationToken _)
    {
        var properties = application.Properties
            .ToImmutableDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
        return new ValueTask<ImmutableDictionary<string, JsonElement>>(properties);
    }

    public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.RedirectUris.ToImmutableArray());

    public ValueTask<ImmutableArray<string>> GetRequirementsAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.Requirements.ToImmutableArray());

    public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(OAuthApplicationState application, CancellationToken _)
        => new(application.Settings.ToImmutableDictionary());

    public ValueTask<OAuthApplicationState> InstantiateAsync(CancellationToken _)
        => new(new OAuthApplicationState { Id = Guid.NewGuid() });

    public async IAsyncEnumerable<OAuthApplicationState> ListAsync(
        int? count, int? offset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OAuthApplicationState>()
            .Where(x => !x.IsDeleted)
            .OrderBy(x => x.Id);

        if (offset.HasValue) query = (IOrderedQueryable<OAuthApplicationState>)query.Skip(offset.Value);
        if (count.HasValue) query = (IOrderedQueryable<OAuthApplicationState>)query.Take(count.Value);

        var applications = await query.ToListAsync(cancellationToken);
        foreach (var application in applications) yield return application;
    }

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OAuthApplicationState>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SetApplicationTypeAsync(OAuthApplicationState a, string? v, CancellationToken _) { a.ApplicationType = v; return default; }
    public ValueTask SetClientIdAsync(OAuthApplicationState a, string? v, CancellationToken _) { a.ClientId = v ?? string.Empty; return default; }
    public ValueTask SetClientSecretAsync(OAuthApplicationState a, string? v, CancellationToken _) { a.PendingClientSecret = v; return default; }
    public ValueTask SetClientTypeAsync(OAuthApplicationState a, string? v, CancellationToken _) { a.ClientType = v; return default; }
    public ValueTask SetConsentTypeAsync(OAuthApplicationState a, string? v, CancellationToken _) { a.ConsentType = v; return default; }
    public ValueTask SetDisplayNameAsync(OAuthApplicationState a, string? v, CancellationToken _) { a.DisplayName = v; return default; }

    public ValueTask SetDisplayNamesAsync(OAuthApplicationState a, ImmutableDictionary<CultureInfo, string> v, CancellationToken _)
    {
        a.DisplayNames = v.ToDictionary(x => x.Key.Name, x => x.Value);
        return default;
    }

    public ValueTask SetJsonWebKeySetAsync(OAuthApplicationState a, JsonWebKeySet? set, CancellationToken _)
    {
        a.PendingJsonWebKeySet = set is null ? null : JsonSerializer.Serialize(set);
        return default;
    }

    public ValueTask SetPermissionsAsync(OAuthApplicationState a, ImmutableArray<string> v, CancellationToken _) { a.Permissions = v.ToList(); return default; }
    public ValueTask SetPostLogoutRedirectUrisAsync(OAuthApplicationState a, ImmutableArray<string> v, CancellationToken _) { a.PostLogoutRedirectUris = v.ToList(); return default; }

    public ValueTask SetPropertiesAsync(OAuthApplicationState a, ImmutableDictionary<string, JsonElement> v, CancellationToken _)
    {
        a.Properties = v.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
        return default;
    }

    public ValueTask SetRedirectUrisAsync(OAuthApplicationState a, ImmutableArray<string> v, CancellationToken _) { a.RedirectUris = v.ToList(); return default; }
    public ValueTask SetRequirementsAsync(OAuthApplicationState a, ImmutableArray<string> v, CancellationToken _) { a.Requirements = v.ToList(); return default; }
    public ValueTask SetSettingsAsync(OAuthApplicationState a, ImmutableDictionary<string, string> v, CancellationToken _) { a.Settings = v.ToDictionary(x => x.Key, x => x.Value); return default; }

    public async ValueTask UpdateAsync(OAuthApplicationState application, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();

        var currentState = await session.LoadAsync<OAuthApplicationState>(application.Id, cancellationToken);
        if (currentState is null) return;

        var aggregate = await session.Events.AggregateStreamAsync<OAuthApplicationAggregate>(application.Id, token: cancellationToken);
        if (aggregate is null) return;

        if (application.DisplayName != currentState.DisplayName)
            session.Events.Append(application.Id, aggregate.SetDisplayName(application.DisplayName));

        if (application.ClientType != currentState.ClientType)
            session.Events.Append(application.Id, aggregate.SetClientType(application.ClientType));

        if (application.ConsentType != currentState.ConsentType)
            session.Events.Append(application.Id, aggregate.SetConsentType(application.ConsentType));

        if (!application.RedirectUris.SequenceEqual(currentState.RedirectUris))
            session.Events.Append(application.Id, aggregate.SetRedirectUris(application.RedirectUris));

        if (!application.PostLogoutRedirectUris.SequenceEqual(currentState.PostLogoutRedirectUris))
            session.Events.Append(application.Id, aggregate.SetPostLogoutRedirectUris(application.PostLogoutRedirectUris));

        if (!application.Permissions.SequenceEqual(currentState.Permissions))
            session.Events.Append(application.Id, aggregate.SetPermissions(application.Permissions));

        if (!application.Requirements.SequenceEqual(currentState.Requirements))
            session.Events.Append(application.Id, aggregate.SetRequirements(application.Requirements));

        if (!DictionaryEquals(application.Settings, currentState.Settings))
            session.Events.Append(application.Id, aggregate.SetSettings(application.Settings));

        if (!DictionaryEquals(application.DisplayNames, currentState.DisplayNames))
            session.Events.Append(application.Id, aggregate.SetDisplayNames(application.DisplayNames));

        if (!DictionaryEquals(application.Properties, currentState.Properties))
            session.Events.Append(application.Id, aggregate.SetProperties(application.Properties));

        if (application.PendingClientSecret is not null || application.PendingJsonWebKeySet is not null)
        {
            var securityData = await session.LoadAsync<OAuthApplicationSecurityData>(application.Id, cancellationToken)
                ?? OAuthApplicationSecurityData.Create(application.Id);

            if (application.PendingClientSecret is not null) securityData.ClientSecret = application.PendingClientSecret;
            if (application.PendingJsonWebKeySet is not null) securityData.JsonWebKeySet = application.PendingJsonWebKeySet;
            securityData.UpdateConcurrencyToken();
            session.Store(securityData);
        }

        await session.SaveChangesAsync(cancellationToken);
    }

    private static bool DictionaryEquals<TKey, TValue>(IDictionary<TKey, TValue> a, IDictionary<TKey, TValue> b)
        where TKey : notnull
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var value) || !Equals(kvp.Value, value)) return false;
        }
        return true;
    }
}
