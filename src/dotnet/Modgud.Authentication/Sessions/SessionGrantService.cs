using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authorization.Apps;
using Modgud.Authorization.Principals;
using Modgud.Domain.OAuth.Applications;
using Modgud.Infrastructure.ChangeFeed;

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

        await RecordRevocationsAsync(work, grants, ct);
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
        await RecordRevocationsAsync(work, grants, ct);
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
        // Logical recovery is limited to seven days; the existing daily session
        // retention job also removes expired recovery metadata, even with no grants.
        var cutoff = clock.GetUtcNow().AddSeconds(-SessionRevocation.RetentionSeconds);
        session.DeleteWhere<SessionRevocation>(r => r.EndedAt < cutoff);
        var sessionIds = await session.Query<SessionGrant>()
            .Select(x => x.SessionId)
            .Distinct()
            .ToListAsync(ct);
        if (sessionIds.Count == 0)
        {
            await session.SaveChangesAsync(ct);
            return 0;
        }

        var live = new HashSet<Guid>();
        live.UnionWith(await session.Query<UserSession>().Where(x => sessionIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct));
        live.UnionWith(await session.Query<ClientSession>().Where(x => sessionIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct));

        var orphaned = sessionIds.Where(id => !live.Contains(id)).ToList();
        if (orphaned.Count == 0)
        {
            await session.SaveChangesAsync(ct);
            return 0;
        }

        var rows = await session.Query<SessionGrant>().Where(x => orphaned.Contains(x.SessionId)).ToListAsync(ct);
        await RecordRevocationsAsync(session, rows, ct);
        foreach (var row in rows) session.Delete(row);
        await session.SaveChangesAsync(ct);
        return rows.Count;
    }

    private async Task RecordRevocationsAsync(IDocumentSession work, IReadOnlyList<SessionGrant> grants, CancellationToken ct)
    {
        if (grants.Count == 0) return;
        var grantIds = grants.Select(g => g.Id).ToList();
        // Preserve historical App attribution for grants already published by
        // the feed, including a user/client that just fell out of current scope.
        var published = await work.Query<AppChangeFeedEntityState>()
            .Where(e => e.EntityKind == "session" && grantIds.Contains(e.EntityId))
            .ToListAsync(ct);
        var pairs = published.Select(e => (e.AppId, SessionId: grants.Single(g => g.Id == e.EntityId).SessionId)).ToHashSet();

        // A grant can end before the asynchronous feed ever published it. In
        // that case apply the same client-association + principal-scope rules
        // as the feed, rather than inferring revocation from snapshot absence.
        var clientIds = grants.Select(g => g.ClientId).Distinct().ToList();
        var clients = await work.Query<OAuthApplicationState>()
            .Where(c => !c.IsDeleted && clientIds.Contains(c.ClientId)).ToListAsync(ct);
        var appIds = clients.SelectMany(c => c.AppIds).Distinct().ToList();
        if (appIds.Count > 0)
        {
            var apps = await work.Query<App>().Where(a => appIds.Contains(a.Id) && !a.IsDeleted).ToListAsync(ct);
            var directory = await work.Query<Principal>().ToListAsync(ct);
            foreach (var app in apps)
            {
                var scopedUsers = ApplicationScopeResolver.BuildSnapshot(app, directory).Principals.Select(p => p.Id).ToHashSet();
                var appClients = clients.Where(c => c.AppIds.Contains(app.Id)).Select(c => c.ClientId).ToHashSet(StringComparer.Ordinal);
                foreach (var grant in grants.Where(g => appClients.Contains(g.ClientId) && scopedUsers.Contains(g.UserId)))
                    pairs.Add((app.Id, grant.SessionId));
            }
        }

        var endedAt = clock.GetUtcNow();
        foreach (var (appId, sid) in pairs)
            work.Store(new SessionRevocation
            {
                Id = Guid.NewGuid(), AppId = appId, SessionId = sid, EndedAt = endedAt,
            });
    }

    /// <summary>One target per relying party; the issuer of the newest grant wins when a
    /// client somehow saw two.</summary>
    private static List<AccessEndTarget> Targets(IEnumerable<SessionGrant> grants) =>
        grants.GroupBy(g => g.ClientId, StringComparer.Ordinal)
            .Select(g => new AccessEndTarget(g.Key, g.OrderByDescending(x => x.LastIssuedAt).First().Issuer))
            .OrderBy(t => t.ClientId, StringComparer.Ordinal)
            .ToList();
}
