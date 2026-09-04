using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using ErrorOr;
using Marten;
using JasperFx;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Realms;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// Marten-backed <see cref="ISessionService"/>. Sessions are tenant-scoped
/// — the injected <see cref="IDocumentSession"/> resolves the active realm
/// via <c>TenantedSessionFactory</c>, so a user's sessions never leak across
/// realms.
/// </summary>
public class SessionService(
    IDocumentSession session,
    ITenantSessionFactory sessionFactory,
    IDeviceInfoService deviceInfo,
    IRealmSettingsService realmSettings,
    IBrowserSessionConnectionRegistry connections,
    ISessionGrantService grants) : ISessionService
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    public async Task<ErrorOr<SessionListDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await session.Query<UserSession>()
            .Where(s => s.UserId == userId && s.ExpiresAt > now && s.AbsoluteExpiresAt > now)
            .OrderByDescending(s => s.LastActiveAt)
            .ToListAsync(ct);

        var dtos = sessions.Select(s => new SessionDto
        {
            Id = s.Id.ToString(),
            IpAddress = s.IpAddress,
            Browser = s.Browser,
            BrowserVersion = s.BrowserVersion,
            OperatingSystem = s.OperatingSystem,
            OsVersion = s.OsVersion,
            DeviceType = s.DeviceType,
            CreatedAt = s.CreatedAt,
            LastActiveAt = s.LastActiveAt,
            IsCurrent = currentSessionId.HasValue && s.Id == currentSessionId.Value,
        }).ToList();

        return new SessionListDto { Sessions = dtos };
    }

    public async Task<ErrorOr<UserSession>> CreateSessionAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var policy = await GetPolicyAsync(ct);
        var device = deviceInfo.Parse();
        var entity = UserSession.Create(
            userId,
            ipAddress,
            userAgent,
            device.Browser,
            device.BrowserVersion,
            device.OperatingSystem,
            device.OsVersion,
            device.DeviceType,
            policy.IdleLifetime,
            policy.AbsoluteLifetime);

        session.Store(entity);
        await session.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<BrowserSessionPolicy> GetPolicyAsync(CancellationToken ct = default)
    {
        var settings = await realmSettings.LoadAsync(ct);
        return settings.BrowserSessions ?? BrowserSessionPolicy.Defaults;
    }

    public async Task<UserSession?> ValidateSessionAsync(
        Guid userId,
        Guid sessionId,
        bool touch,
        CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<UserSession>(sessionId, ct);
        var now = DateTimeOffset.UtcNow;
        if (entity is null || entity.UserId != userId) return null;
        if (!entity.IsActive(now))
        {
            session.Delete(entity);
            await session.SaveChangesAsync(ct);
            connections.Revoke(sessionId);
            return null;
        }

        if (touch && entity.LastActiveAt <= now.Subtract(TouchInterval))
        {
            var policy = await GetPolicyAsync(ct);
            entity.Touch(now, policy.IdleLifetime);
            // This is an update of an authoritative row, never an upsert. If a
            // targeted revoke deleted the row after we loaded it, Update must
            // fail optimistic concurrency instead of resurrecting the session.
            session.Update(entity);
            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (ConcurrencyException)
            {
                // A parallel request may have touched the same active session
                // after both requests loaded the same document version. That is
                // not a revocation and must never turn into a false 401. Re-read
                // through a fresh query session so the committed winner is
                // authoritative: an active row means the concurrent touch won;
                // a missing/expired row means a targeted revoke really won.
                await using var retry = sessionFactory.OpenQuerySession();
                var current = await retry.LoadAsync<UserSession>(sessionId, ct);
                var retryNow = DateTimeOffset.UtcNow;
                return current is not null
                       && current.UserId == userId
                       && current.IsActive(retryNow)
                    ? current
                    : null;
            }
        }

        return entity;
    }

    public Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default) =>
        EndSessionAsync(userId, sessionId, AccessEndReasons.Revoked, initiatingClientId: null, ct);

    public async Task<ErrorOr<bool>> EndSessionAsync(Guid userId, Guid sessionId, string reason, string? initiatingClientId, CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<UserSession>(sessionId, ct);
        if (entity is null) return Error.NotFound("Session.NotFound", $"Session {sessionId} not found.");
        if (entity.UserId != userId) return Error.Forbidden("Session.NotOwner", "Caller does not own this session.");

        session.Delete<UserSession>(sessionId);
        // ADR 0009 — the session's relying parties and the end marker commit with the delete.
        await grants.StageSessionEndAsync(session, userId, sessionId, reason, initiatingClientId, ct);
        await session.SaveChangesAsync(ct);
        connections.Revoke(sessionId);
        return true;
    }

    public async Task<ErrorOr<bool>> RevokeAllSessionsAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct = default)
    {
        var ids = exceptSessionId is { } excludedId
            ? await session.Query<UserSession>()
                .Where(s => s.UserId == userId && s.Id != excludedId)
                .Select(s => s.Id)
                .ToListAsync(ct)
            : await session.Query<UserSession>()
                .Where(s => s.UserId == userId)
                .Select(s => s.Id)
                .ToListAsync(ct);

        if (exceptSessionId is { } excludedSessionId)
            session.DeleteWhere<UserSession>(s => s.UserId == userId && s.Id != excludedSessionId);
        else
            session.DeleteWhere<UserSession>(s => s.UserId == userId);

        // ADR 0009 — one end marker per session so every relying party of every
        // ended session is notified with the sid it knows (a user-level end would
        // also log the caller's own, kept session out at its RPs).
        foreach (var id in ids)
            await grants.StageSessionEndAsync(session, userId, id, AccessEndReasons.Revoked, initiatingClientId: null, ct);

        await session.SaveChangesAsync(ct);
        foreach (var id in ids) connections.Revoke(id);
        return true;
    }

    public async Task TouchSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<UserSession>(sessionId, ct);
        if (entity is null) return;
        var policy = await GetPolicyAsync(ct);
        entity.Touch(DateTimeOffset.UtcNow, policy.IdleLifetime);
        // Same revoke-wins guarantee as ValidateSessionAsync: a delayed touch
        // must not re-insert a session that was concurrently deleted.
        session.Update(entity);
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            // Revocation won the race.
        }
    }

    public async Task<int> PruneExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await session.Query<UserSession>()
            .Where(s => s.ExpiresAt <= now || s.AbsoluteExpiresAt <= now)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (expired.Count == 0) return 0;

        var owners = await session.Query<UserSession>()
            .Where(s => s.ExpiresAt <= now || s.AbsoluteExpiresAt <= now)
            .Select(s => new { s.Id, s.UserId })
            .ToListAsync(ct);
        session.DeleteWhere<UserSession>(s => s.ExpiresAt <= now || s.AbsoluteExpiresAt <= now);
        // ADR 0009 — an expired session ends its relying-party sessions too.
        foreach (var owner in owners)
            await grants.StageSessionEndAsync(session, owner.UserId, owner.Id, AccessEndReasons.Expired, initiatingClientId: null, ct);
        await session.SaveChangesAsync(ct);
        foreach (var id in expired) connections.Revoke(id);
        return expired.Count;
    }
}
