using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Modgud.Domain.OAuth.Scopes;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using OpenIddict.Abstractions;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict scope store using Marten event sourcing.
/// Scopes contain no sensitive data — everything is event-sourced.
/// Sessions are tenant-scoped via <see cref="ITenantSessionFactory"/>.
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
        return await session.Query<OAuthScopeState>().Where(x => !x.IsDeleted).CountAsync(cancellationToken);
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

        var (_, createdEvent) = OAuthScopeAggregate.Create(
            scope.Id, scope.Name, scope.DisplayName, scope.Description, scope.Resources);

        session.Events.StartStream<OAuthScopeAggregate>(scope.Id, createdEvent);

        if (scope.DisplayNames.Count > 0)
            session.Events.Append(scope.Id, new OAuthScopeDisplayNamesChanged(scope.Id, scope.DisplayNames));

        if (scope.Descriptions.Count > 0)
            session.Events.Append(scope.Id, new OAuthScopeDescriptionsChanged(scope.Id, scope.Descriptions));

        if (scope.Properties.Count > 0)
            session.Events.Append(scope.Id, new OAuthScopePropertiesChanged(scope.Id, scope.Properties));

        // Pull cocoar-specific scope properties (Enabled/Required/...) out of the
        // generic Properties bag into first-class fields/events.
        ExtractAndApplyIdentityResourceProperties(scope);

        if (!scope.Enabled)
            session.Events.Append(scope.Id, new OAuthScopeEnabledChanged(scope.Id, scope.Enabled));

        if (scope.Required)
            session.Events.Append(scope.Id, new OAuthScopeRequiredChanged(scope.Id, scope.Required));

        if (scope.Emphasize)
            session.Events.Append(scope.Id, new OAuthScopeEmphasizeChanged(scope.Id, scope.Emphasize));

        if (!scope.ShowInDiscoveryDocument)
            session.Events.Append(scope.Id, new OAuthScopeShowInDiscoveryDocumentChanged(scope.Id, scope.ShowInDiscoveryDocument));

        if (scope.UserClaims.Count > 0)
            session.Events.Append(scope.Id, new OAuthScopeUserClaimsChanged(scope.Id, scope.UserClaims));

        await session.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DeleteAsync(OAuthScopeState scope, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        var aggregate = await session.Events.AggregateStreamAsync<OAuthScopeAggregate>(scope.Id, token: cancellationToken);
        if (aggregate is not null && !aggregate.IsDeleted)
        {
            session.Events.Append(scope.Id, aggregate.Delete());
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<OAuthScopeState?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(identifier, out var id)) return null;
        await using var session = _sessionFactory.OpenQuerySession();
        var state = await session.LoadAsync<OAuthScopeState>(id, cancellationToken);
        return state?.IsDeleted == true ? null : state;
    }

    public async ValueTask<OAuthScopeState?> FindByNameAsync(string name, CancellationToken cancellationToken)
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

        foreach (var scope in scopes) yield return scope;
    }

    public async IAsyncEnumerable<OAuthScopeState> FindByResourceAsync(
        string resource,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var scopes = await session.Query<OAuthScopeState>()
            .Where(x => x.Resources.Contains(resource) && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var scope in scopes) yield return scope;
    }

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OAuthScopeState>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<string?> GetDescriptionAsync(OAuthScopeState scope, CancellationToken _) => new(scope.Description);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDescriptionsAsync(OAuthScopeState scope, CancellationToken _)
    {
        var descriptions = scope.Descriptions
            .ToImmutableDictionary(x => CultureInfo.GetCultureInfo(x.Key), x => x.Value);
        return new ValueTask<ImmutableDictionary<CultureInfo, string>>(descriptions);
    }

    public ValueTask<string?> GetDisplayNameAsync(OAuthScopeState scope, CancellationToken _) => new(scope.DisplayName);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(OAuthScopeState scope, CancellationToken _)
    {
        var displayNames = scope.DisplayNames
            .ToImmutableDictionary(x => CultureInfo.GetCultureInfo(x.Key), x => x.Value);
        return new ValueTask<ImmutableDictionary<CultureInfo, string>>(displayNames);
    }

    public ValueTask<string?> GetIdAsync(OAuthScopeState scope, CancellationToken _) => new(scope.Id.ToString());
    public ValueTask<string?> GetNameAsync(OAuthScopeState scope, CancellationToken _) => new(scope.Name);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OAuthScopeState scope, CancellationToken _)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        foreach (var kvp in scope.Properties)
        {
            builder[kvp.Key] = JsonSerializer.SerializeToElement(kvp.Value);
        }
        builder[ScopePropertyKeys.Enabled] = JsonSerializer.SerializeToElement(scope.Enabled);
        builder[ScopePropertyKeys.Required] = JsonSerializer.SerializeToElement(scope.Required);
        builder[ScopePropertyKeys.Emphasize] = JsonSerializer.SerializeToElement(scope.Emphasize);
        builder[ScopePropertyKeys.ShowInDiscoveryDocument] = JsonSerializer.SerializeToElement(scope.ShowInDiscoveryDocument);
        builder[ScopePropertyKeys.UserClaims] = JsonSerializer.SerializeToElement(scope.UserClaims);
        return new ValueTask<ImmutableDictionary<string, JsonElement>>(builder.ToImmutable());
    }

    public ValueTask<ImmutableArray<string>> GetResourcesAsync(OAuthScopeState scope, CancellationToken _)
        => new(scope.Resources.ToImmutableArray());

    public ValueTask<OAuthScopeState> InstantiateAsync(CancellationToken _)
        => new(new OAuthScopeState { Id = Guid.NewGuid() });

    public async IAsyncEnumerable<OAuthScopeState> ListAsync(
        int? count, int? offset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OAuthScopeState>().Where(x => !x.IsDeleted).OrderBy(x => x.Id);

        if (offset.HasValue) query = (IOrderedQueryable<OAuthScopeState>)query.Skip(offset.Value);
        if (count.HasValue) query = (IOrderedQueryable<OAuthScopeState>)query.Take(count.Value);

        var scopes = await query.ToListAsync(cancellationToken);
        foreach (var scope in scopes) yield return scope;
    }

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OAuthScopeState>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask SetDescriptionAsync(OAuthScopeState s, string? v, CancellationToken _) { s.Description = v; return default; }

    public ValueTask SetDescriptionsAsync(OAuthScopeState s, ImmutableDictionary<CultureInfo, string> v, CancellationToken _)
    {
        s.Descriptions = v.ToDictionary(x => x.Key.Name, x => x.Value);
        return default;
    }

    public ValueTask SetDisplayNameAsync(OAuthScopeState s, string? v, CancellationToken _) { s.DisplayName = v; return default; }

    public ValueTask SetDisplayNamesAsync(OAuthScopeState s, ImmutableDictionary<CultureInfo, string> v, CancellationToken _)
    {
        s.DisplayNames = v.ToDictionary(x => x.Key.Name, x => x.Value);
        return default;
    }

    public ValueTask SetNameAsync(OAuthScopeState s, string? v, CancellationToken _) { s.Name = v ?? string.Empty; return default; }

    public ValueTask SetPropertiesAsync(OAuthScopeState s, ImmutableDictionary<string, JsonElement> v, CancellationToken _)
    {
        s.Properties = v.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
        return default;
    }

    public ValueTask SetResourcesAsync(OAuthScopeState s, ImmutableArray<string> v, CancellationToken _) { s.Resources = v.ToList(); return default; }

    public async ValueTask UpdateAsync(OAuthScopeState scope, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();

        var currentState = await session.LoadAsync<OAuthScopeState>(scope.Id, cancellationToken);
        if (currentState is null) return;

        var aggregate = await session.Events.AggregateStreamAsync<OAuthScopeAggregate>(scope.Id, token: cancellationToken);
        if (aggregate is null) return;

        ExtractAndApplyIdentityResourceProperties(scope);

        if (scope.DisplayName != currentState.DisplayName)
            session.Events.Append(scope.Id, aggregate.SetDisplayName(scope.DisplayName));

        if (scope.Description != currentState.Description)
            session.Events.Append(scope.Id, aggregate.SetDescription(scope.Description));

        if (!scope.Resources.SequenceEqual(currentState.Resources))
            session.Events.Append(scope.Id, aggregate.SetResources(scope.Resources));

        if (!DictionaryEquals(scope.DisplayNames, currentState.DisplayNames))
            session.Events.Append(scope.Id, aggregate.SetDisplayNames(scope.DisplayNames));

        if (!DictionaryEquals(scope.Descriptions, currentState.Descriptions))
            session.Events.Append(scope.Id, aggregate.SetDescriptions(scope.Descriptions));

        if (!DictionaryEquals(scope.Properties, currentState.Properties))
            session.Events.Append(scope.Id, aggregate.SetProperties(scope.Properties));

        if (scope.Enabled != currentState.Enabled)
            session.Events.Append(scope.Id, aggregate.SetEnabled(scope.Enabled));

        if (scope.Required != currentState.Required)
            session.Events.Append(scope.Id, aggregate.SetRequired(scope.Required));

        if (scope.Emphasize != currentState.Emphasize)
            session.Events.Append(scope.Id, aggregate.SetEmphasize(scope.Emphasize));

        if (scope.ShowInDiscoveryDocument != currentState.ShowInDiscoveryDocument)
            session.Events.Append(scope.Id, aggregate.SetShowInDiscoveryDocument(scope.ShowInDiscoveryDocument));

        if (!scope.UserClaims.SequenceEqual(currentState.UserClaims))
            session.Events.Append(scope.Id, aggregate.SetUserClaims(scope.UserClaims));

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Pulls cocoar-specific scope properties (cocoar:enabled, cocoar:required, ...)
    /// out of the generic Properties bag into first-class fields, so they get their
    /// own dedicated events.
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

    private static bool TryGetJsonProperty(IDictionary<string, object?> properties, string key, out JsonElement element)
    {
        if (properties.TryGetValue(key, out var value))
        {
            if (value is JsonElement je) { element = je; return true; }
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
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var value) || !Equals(kvp.Value, value)) return false;
        }
        return true;
    }
}
