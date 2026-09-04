using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;

namespace Modgud.Authentication.Sessions;

public sealed class SessionGrantService(IDocumentSession session, TimeProvider clock) : ISessionGrantService
{
    public async Task RecordIssuanceAsync(
        Guid sessionId,
        Guid userId,
        string clientId,
        string applicationId,
        AccessSessionKind kind,
        string issuer,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var id = SessionGrant.IdFor(sessionId, clientId);
        var existing = await session.LoadAsync<SessionGrant>(id, ct);
        if (existing is null)
        {
            session.Store(new SessionGrant
            {
                Id = id,
                SessionId = sessionId,
                UserId = userId,
                ClientId = clientId,
                ApplicationId = applicationId,
                Kind = kind,
                Issuer = issuer,
                FirstIssuedAt = now,
                LastIssuedAt = now,
            });
            session.Events.Append(userId, new UserAccessGrantedEvent(userId, sessionId, clientId, kind, now));
        }
        else
        {
            existing.LastIssuedAt = now;
            existing.Issuer = issuer;
            session.Store(existing);
        }

        await session.SaveChangesAsync(ct);
    }

    public async Task<int> StageSessionEndAsync(
        IDocumentSession work,
        Guid userId,
        Guid sessionId,
        string reason,
        string? initiatingClientId,
        CancellationToken ct = default)
    {
        var grants = await work.Query<SessionGrant>()
            .Where(x => x.SessionId == sessionId)
            .ToListAsync(ct);
        if (grants.Count == 0) return 0;

        foreach (var grant in grants) work.Delete(grant);
        work.Events.Append(userId, new UserAccessEndedEvent(
            userId,
            AccessEndScope.Session,
            sessionId,
            Targets(grants),
            initiatingClientId,
            reason,
            clock.GetUtcNow()));
        return grants.Count;
    }

    public async Task<int> StageUserEndAsync(
        IDocumentSession work,
        Guid userId,
        string reason,
        CancellationToken ct = default)
    {
        var grants = await work.Query<SessionGrant>()
            .Where(x => x.UserId == userId)
            .ToListAsync(ct);
        foreach (var grant in grants) work.Delete(grant);
        work.Events.Append(userId, new UserAccessEndedEvent(
            userId,
            AccessEndScope.User,
            null,
            Targets(grants),
            null,
            reason,
            clock.GetUtcNow()));
        return grants.Count;
    }

    public async Task<int> SweepOrphansAsync(CancellationToken ct = default)
    {
        var sessionIds = await session.Query<SessionGrant>()
            .Select(x => x.SessionId)
            .Distinct()
            .ToListAsync(ct);
        if (sessionIds.Count == 0) return 0;

        var live = new HashSet<Guid>();
        live.UnionWith(await session.Query<UserSession>().Where(x => sessionIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct));
        live.UnionWith(await session.Query<ClientSession>().Where(x => sessionIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct));

        var orphaned = sessionIds.Where(id => !live.Contains(id)).ToList();
        if (orphaned.Count == 0) return 0;

        var rows = await session.Query<SessionGrant>().Where(x => orphaned.Contains(x.SessionId)).ToListAsync(ct);
        foreach (var row in rows) session.Delete(row);
        await session.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>One target per relying party; the issuer of the newest grant wins when a
    /// client somehow saw two.</summary>
    private static List<AccessEndTarget> Targets(IEnumerable<SessionGrant> grants) =>
        grants.GroupBy(g => g.ClientId, StringComparer.Ordinal)
            .Select(g => new AccessEndTarget(g.Key, g.OrderByDescending(x => x.LastIssuedAt).First().Issuer))
            .OrderBy(t => t.ClientId, StringComparer.Ordinal)
            .ToList();
}
