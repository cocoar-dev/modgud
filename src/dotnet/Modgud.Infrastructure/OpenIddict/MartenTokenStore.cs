using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using OpenIddict.Abstractions;

namespace Modgud.Infrastructure.OpenIddict;

/// <summary>
/// Custom OpenIddict token store using Marten document storage.
/// Sessions are tenant-scoped via <see cref="ITenantSessionFactory"/>.
/// </summary>
public class MartenTokenStore : IOpenIddictTokenStore<OpenIddictTokenDocument>
{
    private readonly ITenantSessionFactory _sessionFactory;

    public MartenTokenStore(ITenantSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory;
    }

    public async ValueTask<long> CountAsync(CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return await session.Query<OpenIddictTokenDocument>().CountAsync(cancellationToken);
    }

    public async ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<OpenIddictTokenDocument>, IQueryable<TResult>> query,
        CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return query(session.Query<OpenIddictTokenDocument>()).LongCount();
    }

    public async ValueTask CreateAsync(OpenIddictTokenDocument token, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        session.Store(token);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DeleteAsync(OpenIddictTokenDocument token, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        session.Delete(token);
        await session.SaveChangesAsync(cancellationToken);
    }

    public async IAsyncEnumerable<OpenIddictTokenDocument> FindAsync(
        string? subject, string? client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictTokenDocument>().AsQueryable();
        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        var tokens = await query.ToListAsync(cancellationToken);
        foreach (var token in tokens) yield return token;
    }

    public async IAsyncEnumerable<OpenIddictTokenDocument> FindAsync(
        string? subject, string? client, string? status,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictTokenDocument>().AsQueryable();
        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        var tokens = await query.ToListAsync(cancellationToken);
        foreach (var token in tokens) yield return token;
    }

    public async IAsyncEnumerable<OpenIddictTokenDocument> FindAsync(
        string? subject, string? client, string? status, string? type,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictTokenDocument>().AsQueryable();
        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(type)) query = query.Where(x => x.Type == type);
        var tokens = await query.ToListAsync(cancellationToken);
        foreach (var token in tokens) yield return token;
    }

    public async IAsyncEnumerable<OpenIddictTokenDocument> FindByApplicationIdAsync(
        string identifier,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var tokens = await session.Query<OpenIddictTokenDocument>()
            .Where(x => x.ApplicationId == identifier)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens) yield return token;
    }

    public async IAsyncEnumerable<OpenIddictTokenDocument> FindByAuthorizationIdAsync(
        string identifier,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var tokens = await session.Query<OpenIddictTokenDocument>()
            .Where(x => x.AuthorizationId == identifier)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens) yield return token;
    }

    public async ValueTask<OpenIddictTokenDocument?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return await session.LoadAsync<OpenIddictTokenDocument>(identifier, cancellationToken);
    }

    public async ValueTask<OpenIddictTokenDocument?> FindByReferenceIdAsync(string identifier, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        return await session.Query<OpenIddictTokenDocument>()
            .FirstOrDefaultAsync(x => x.ReferenceId == identifier, cancellationToken);
    }

    public async IAsyncEnumerable<OpenIddictTokenDocument> FindBySubjectAsync(
        string subject,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var tokens = await session.Query<OpenIddictTokenDocument>()
            .Where(x => x.Subject == subject)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens) yield return token;
    }

    public ValueTask<string?> GetApplicationIdAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.ApplicationId);

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<OpenIddictTokenDocument>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken) => throw new NotSupportedException();

    public ValueTask<string?> GetAuthorizationIdAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.AuthorizationId);
    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.CreationDate);
    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.ExpirationDate);
    public ValueTask<string?> GetIdAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.Id);
    public ValueTask<string?> GetPayloadAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.Payload);

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OpenIddictTokenDocument t, CancellationToken _)
    {
        var properties = t.Properties.ToImmutableDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value));
        return new ValueTask<ImmutableDictionary<string, JsonElement>>(properties);
    }

    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.RedemptionDate);
    public ValueTask<string?> GetReferenceIdAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.ReferenceId);
    public ValueTask<string?> GetStatusAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.Status);
    public ValueTask<string?> GetSubjectAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.Subject);
    public ValueTask<string?> GetTypeAsync(OpenIddictTokenDocument t, CancellationToken _) => new(t.Type);

    public ValueTask<OpenIddictTokenDocument> InstantiateAsync(CancellationToken _) => new(new OpenIddictTokenDocument());

    public async IAsyncEnumerable<OpenIddictTokenDocument> ListAsync(
        int? count, int? offset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenQuerySession();
        var query = session.Query<OpenIddictTokenDocument>().OrderBy(x => x.Id);

        if (offset.HasValue) query = (IOrderedQueryable<OpenIddictTokenDocument>)query.Skip(offset.Value);
        if (count.HasValue) query = (IOrderedQueryable<OpenIddictTokenDocument>)query.Take(count.Value);

        var tokens = await query.ToListAsync(cancellationToken);
        foreach (var token in tokens) yield return token;
    }

    public IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<OpenIddictTokenDocument>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken) => throw new NotSupportedException();

    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        var tokens = await session.Query<OpenIddictTokenDocument>()
            .Where(x => x.CreationDate < threshold &&
                (x.Status == OpenIddictConstants.Statuses.Inactive ||
                 x.Status == OpenIddictConstants.Statuses.Revoked ||
                 x.ExpirationDate < DateTimeOffset.UtcNow))
            .ToListAsync(cancellationToken);

        foreach (var token in tokens) session.Delete(token);
        await session.SaveChangesAsync(cancellationToken);
        return tokens.Count;
    }

    public async ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken)
    {
        await using var session = _sessionFactory.OpenSession();
        var query = session.Query<OpenIddictTokenDocument>().AsQueryable();
        if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
        if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
        if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(type)) query = query.Where(x => x.Type == type);

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
        await using var session = _sessionFactory.OpenSession();
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
        await using var session = _sessionFactory.OpenSession();
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
        await using var session = _sessionFactory.OpenSession();
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

    public ValueTask SetApplicationIdAsync(OpenIddictTokenDocument t, string? v, CancellationToken _) { t.ApplicationId = v; return default; }
    public ValueTask SetAuthorizationIdAsync(OpenIddictTokenDocument t, string? v, CancellationToken _) { t.AuthorizationId = v; return default; }
    public ValueTask SetCreationDateAsync(OpenIddictTokenDocument t, DateTimeOffset? v, CancellationToken _) { t.CreationDate = v; return default; }
    public ValueTask SetExpirationDateAsync(OpenIddictTokenDocument t, DateTimeOffset? v, CancellationToken _) { t.ExpirationDate = v; return default; }
    public ValueTask SetPayloadAsync(OpenIddictTokenDocument t, string? v, CancellationToken _) { t.Payload = v; return default; }

    public ValueTask SetPropertiesAsync(OpenIddictTokenDocument t, ImmutableDictionary<string, JsonElement> v, CancellationToken _)
    {
        t.Properties = v.ToDictionary(x => x.Key, x => (object?)x.Value.Deserialize<object>());
        return default;
    }

    public ValueTask SetRedemptionDateAsync(OpenIddictTokenDocument t, DateTimeOffset? v, CancellationToken _) { t.RedemptionDate = v; return default; }
    public ValueTask SetReferenceIdAsync(OpenIddictTokenDocument t, string? v, CancellationToken _) { t.ReferenceId = v; return default; }
    public ValueTask SetStatusAsync(OpenIddictTokenDocument t, string? v, CancellationToken _) { t.Status = v; return default; }
    public ValueTask SetSubjectAsync(OpenIddictTokenDocument t, string? v, CancellationToken _) { t.Subject = v; return default; }
    public ValueTask SetTypeAsync(OpenIddictTokenDocument t, string? v, CancellationToken _) { t.Type = v; return default; }

    public async ValueTask UpdateAsync(OpenIddictTokenDocument token, CancellationToken cancellationToken)
    {
        token.ConcurrencyToken = Guid.NewGuid().ToString();
        await using var session = _sessionFactory.OpenSession();
        session.Store(token);
        await session.SaveChangesAsync(cancellationToken);
    }
}
