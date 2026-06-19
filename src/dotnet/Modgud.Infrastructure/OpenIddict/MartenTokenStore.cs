using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Modgud.Domain.OAuth.Storage;
using Modgud.Infrastructure.Persistence.Tenancy;
using Marten;
using JasperFx;
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
        var now = DateTimeOffset.UtcNow;
        return await RetryOnConflictAsync(async session =>
        {
            var tokens = await session.Query<OpenIddictTokenDocument>()
                .Where(x => x.CreationDate < threshold &&
                    (x.Status == OpenIddictConstants.Statuses.Inactive ||
                     x.Status == OpenIddictConstants.Statuses.Revoked ||
                     x.ExpirationDate < now))
                .ToListAsync(cancellationToken);

            foreach (var token in tokens) session.Delete(token);
            return tokens.Count;
        }, cancellationToken);
    }

    public ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken cancellationToken) =>
        RevokeMatchingAsync(query =>
        {
            if (!string.IsNullOrEmpty(subject)) query = query.Where(x => x.Subject == subject);
            if (!string.IsNullOrEmpty(client)) query = query.Where(x => x.ApplicationId == client);
            if (!string.IsNullOrEmpty(status)) query = query.Where(x => x.Status == status);
            if (!string.IsNullOrEmpty(type)) query = query.Where(x => x.Type == type);
            return query;
        }, cancellationToken);

    public ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken) =>
        RevokeMatchingAsync(query => query.Where(x => x.ApplicationId == identifier), cancellationToken);

    public ValueTask<long> RevokeByAuthorizationIdAsync(string identifier, CancellationToken cancellationToken) =>
        RevokeMatchingAsync(query => query.Where(x => x.AuthorizationId == identifier), cancellationToken);

    public ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken) =>
        RevokeMatchingAsync(query => query.Where(x => x.Subject == subject), cancellationToken);

    /// <summary>
    /// Load the matching tokens, flip them to Revoked, and persist — retrying the
    /// whole load-and-store on a Marten <see cref="ConcurrencyException"/>. Audit #22
    /// enabled optimistic concurrency on the token document, so a revoke sweep that
    /// races a concurrent refresh-token rotation (which advances a loaded row's
    /// version) would otherwise throw. Revoke is idempotent and monotonic
    /// (Valid→Revoked), so a fresh reload + re-store converges. Best-effort callers
    /// (reuse teardown) already swallow failures; the lifecycle revoker
    /// (logout-all / deactivate) relies on this retry so a benign race doesn't 500.
    ///
    /// <para>Audit #23 residual: a token INSERTED after this sweep's SELECT (not a
    /// modification of a loaded row) is not a version conflict, so the retry does
    /// not pick it up — it can survive the sweep. This stays Low because lifecycle
    /// revocation rotates the user's security stamp first, so any refresh token
    /// minted in that gap fails the OAUTH-07 parity check on its next use anyway.</para>
    /// </summary>
    private async ValueTask<long> RevokeMatchingAsync(
        Func<IQueryable<OpenIddictTokenDocument>, IQueryable<OpenIddictTokenDocument>> filter,
        CancellationToken cancellationToken) =>
        await RetryOnConflictAsync(async session =>
        {
            var tokens = await filter(session.Query<OpenIddictTokenDocument>().AsQueryable())
                .ToListAsync(cancellationToken);
            foreach (var token in tokens)
            {
                token.Status = OpenIddictConstants.Statuses.Revoked;
                session.Store(token);
            }
            return tokens.Count;
        }, cancellationToken);

    private async ValueTask<long> RetryOnConflictAsync(
        Func<IDocumentSession, Task<int>> work, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            await using var session = _sessionFactory.OpenSession();
            var affected = await work(session);
            if (affected == 0) return 0;
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return affected;
            }
            catch (ConcurrencyException) when (attempt < maxAttempts)
            {
                // Reload + redo against a fresh session on the next loop turn.
            }
        }
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
        await using var session = _sessionFactory.OpenSession();

        // Audit #22 — guard the refresh-token redeem against a concurrent replay.
        // OpenIddict loaded `token` in another session and mutated it; re-load the
        // live row HERE (so Marten's same-session optimistic concurrency engages on
        // the store) and compare the ConcurrencyToken the caller saw against the
        // live one. A mismatch means another redeem already rotated the row between
        // the caller's load and now → the caller is stale and must lose. The
        // same-session version check then closes the tiny load→save window so two
        // racers that both passed the comparison still can't both commit. Either
        // failure surfaces as OpenIddict's ConcurrencyException so the token
        // manager rejects this racer with invalid_grant rather than minting a
        // second token pair (RFC 6749 §10.4 reuse defense).
        var current = await session.LoadAsync<OpenIddictTokenDocument>(token.Id, cancellationToken);
        if (current is null || !string.Equals(current.ConcurrencyToken, token.ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new OpenIddictExceptions.ConcurrencyException(
                "The token was concurrently updated and cannot be persisted.");
        }

        token.ConcurrencyToken = Guid.NewGuid().ToString();
        session.Store(token);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException ex)
        {
            throw new OpenIddictExceptions.ConcurrencyException(
                "The token was concurrently updated and cannot be persisted.", ex);
        }
    }
}
