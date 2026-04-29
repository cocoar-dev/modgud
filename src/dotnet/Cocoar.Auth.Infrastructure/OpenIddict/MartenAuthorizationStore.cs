using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cocoar.Auth.Domain.OAuth.Storage;
using Cocoar.Auth.Infrastructure.Persistence.Tenancy;
using Marten;
using OpenIddict.Abstractions;

namespace Cocoar.Auth.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict authorization store using Marten document storage.
/// Sessions are tenant-scoped via <see cref="ITenantSessionFactory"/>.
/// </summary>
public class MartenAuthorizationStore : IOpenIddictAuthorizationStore<OpenIddictAuthorizationDocument>
{
    private readonly ITenantSessionFactory _sessionFactory;

    public MartenAuthorizationStore(ITenantSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
    }

    public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return await session.Query<OpenIddictAuthorizationDocument>().CountAsync(cancellationToken);
    }

    public async ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OpenIddictAuthorizationDocument>, IQueryable<TResult>> query,
        CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return query(session.Query<OpenIddictAuthorizationDocument>()).LongCount();
    }

    public async ValueTask CreateAsync(OpenIddictAuthorizationDocument authorization, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        session.Store(authorization);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DeleteAsync(OpenIddictAuthorizationDocument authorization, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        session.Delete(authorization);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
        string? subject, string? client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);

        var authorizations = await query.ToListAsync(cancellationToken);
        foreach (var authorization in authorizations) yield return authorization;
    }

    public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
        string? subject, string? client, string? status,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);

        var authorizations = await query.ToListAsync(cancellationToken);
        foreach (var authorization in authorizations) yield return authorization;
    }

    public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
        string? subject, string? client, string? status, string? type,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(type)) query = query.Where(x => x.Type == type);

        var authorizations = await query.ToListAsync(cancellationToken);
        foreach (var authorization in authorizations) yield return authorization;
    }

    public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindAsync(
        string? subject, string? client, string? status, string? type, ImmutableArray<string>? scopes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(type)) query = query.Where(x => x.Type == type);

        var authorizations = await query.ToListAsync(cancellationToken);

        // Scope set filter is in-memory — Marten Linq cannot translate set ops.
        foreach (var authorization in authorizations)
        {
            if (scopes is null || scopes.Value.IsDefault || scopes.Value.All(s => authorization.Scopes.Contains(s)))
                yield return authorization;
        }
    }

    public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindByApplicationIdAsync(
        string identifier,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
            .Where(x => x.ApplicationId == identifier)
            .ToListAsync(cancellationToken);
        foreach (var authorization in authorizations) yield return authorization;
    }

    public async ValueTask<OpenIddictAuthorizationDocument?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return await session.LoadAsync<OpenIddictAuthorizationDocument>(identifier, cancellationToken);
    }

    public async IAsyncEnumerable<OpenIddictAuthorizationDocument> FindBySubjectAsync(
        string subject,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
            .Where(x => x.Subject == subject)
            .ToListAsync(cancellationToken);
        foreach (var authorization in authorizations) yield return authorization;
    }

    public ValueTask<string?> GetApplicationIdAsync(OpenIddictAuthorizationDocument a, CancellationToken _) => new(a.ApplicationId);

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OpenIddictAuthorizationDocument>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OpenIddictAuthorizationDocument a, CancellationToken _) => new(a.CreationDate);
    public ValueTask<string?> GetIdAsync(OpenIddictAuthorizationDocument a, CancellationToken _) => new(a.Id);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictAuthorizationDocument a, CancellationToken _)
    {
        var properties = a.Properties.ToImmutableDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
        return new ValueTask<ImmutableDictionary<string, JsonElement>>(properties);
    }

    public ValueTask<ImmutableArray<string>> GetScopesAsync(OpenIddictAuthorizationDocument a, CancellationToken _) => new(a.Scopes.ToImmutableArray());
    public ValueTask<string?> GetStatusAsync(OpenIddictAuthorizationDocument a, CancellationToken _) => new(a.Status);
    public ValueTask<string?> GetSubjectAsync(OpenIddictAuthorizationDocument a, CancellationToken _) => new(a.Subject);
    public ValueTask<string?> GetTypeAsync(OpenIddictAuthorizationDocument a, CancellationToken _) => new(a.Type);

    public ValueTask<OpenIddictAuthorizationDocument> InstantiateAsync(CancellationToken _) => new(new OpenIddictAuthorizationDocument());

    public async IAsyncEnumerable<OpenIddictAuthorizationDocument> ListAsync(
        int? count, int? offset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictAuthorizationDocument>().OrderBy(x => x.Id);

        if (offset.HasValue) query = (IOrderedQueryable<OpenIddictAuthorizationDocument>)query.Skip(offset.Value);
        if (count.HasValue) query = (IOrderedQueryable<OpenIddictAuthorizationDocument>)query.Take(count.Value);

        var authorizations = await query.ToListAsync(cancellationToken);
        foreach (var authorization in authorizations) yield return authorization;
    }

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OpenIddictAuthorizationDocument>, TState, IQueryable<TResult>> query,
        TState state,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        var authorizations = await session.Query<OpenIddictAuthorizationDocument>()
            .Where(x => x.CreationDate < threshold &&
                (x.Status == OpenIddictConstants.Statuses.Inactive ||
                 x.Status == OpenIddictConstants.Statuses.Revoked))
            .ToListAsync(cancellationToken);

        foreach (var authorization in authorizations) session.Delete(authorization);
        await session.SaveChangesAsync(cancellationToken);
        return authorizations.Count;
    }

    public async ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        var query = session.Query<OpenIddictAuthorizationDocument>().AsQueryable();

        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(type)) query = query.Where(x => x.Type == type);

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
        await using var session = _sessionFactory.OpenSession();
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
        await using var session = _sessionFactory.OpenSession();
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

    public ValueTask SetApplicationIdAsync(OpenIddictAuthorizationDocument a, string? v, CancellationToken _) { a.ApplicationId = v; return default; }
    public ValueTask SetCreationDateAsync(OpenIddictAuthorizationDocument a, DateTimeOffset? v, CancellationToken _) { a.CreationDate = v; return default; }

    public ValueTask SetPropertiesAsync(OpenIddictAuthorizationDocument a, ImmutableDictionary<string, JsonElement> v, CancellationToken _)
    {
        a.Properties = v.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
        return default;
    }

    public ValueTask SetScopesAsync(OpenIddictAuthorizationDocument a, ImmutableArray<string> v, CancellationToken _) { a.Scopes = v.ToHashSet(); return default; }
    public ValueTask SetStatusAsync(OpenIddictAuthorizationDocument a, string? v, CancellationToken _) { a.Status = v; return default; }
    public ValueTask SetSubjectAsync(OpenIddictAuthorizationDocument a, string? v, CancellationToken _) { a.Subject = v; return default; }
    public ValueTask SetTypeAsync(OpenIddictAuthorizationDocument a, string? v, CancellationToken _) { a.Type = v; return default; }

    public async ValueTask UpdateAsync(OpenIddictAuthorizationDocument authorization, CancellationToken cancellationToken)
    {
        authorization.ConcurrencyToken = Guid.NewGuid().ToString();
        await using var session = _sessionFactory.OpenSession();
        session.Store(authorization);
        await session.SaveChangesAsync(cancellationToken);
    }
}
