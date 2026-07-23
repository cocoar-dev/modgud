using Modgud.Authentication.Domain;
using ErrorOr;
using Marten;
using JasperFx;
using Modgud.Authentication.RealmSettings;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// Marten-backed <see cref="ISessionService"/>. Sessions are tenant-scoped
/// — the injected <see cref="IDocumentSession"/> resolves the active realm
/// via <c>TenantedSessionFactory</c>, so a user's sessions never leak across
/// realms.
/// </summary>
public class SessionService(
    IDocumentSession session,
    IDeviceInfoService deviceInfo,
    IRealmSettingsService realmSettings,
    IBrowserSessionConnectionRegistry connections) : ISessionService
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
            session.Store(entity);
            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (ConcurrencyException)
            {
                // A concurrent targeted revoke wins. Never re-insert a deleted row.
                return null;
            }
        }

        return entity;
    }

    public async Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<UserSession>(sessionId, ct);
        if (entity is null) return Error.NotFound("Session.NotFound", $"Session {sessionId} not found.");
        if (entity.UserId != userId) return Error.Forbidden("Session.NotOwner", "Caller does not own this session.");

        session.Delete<UserSession>(sessionId);
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
        session.Store(entity);
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

        session.DeleteWhere<UserSession>(s => s.ExpiresAt <= now || s.AbsoluteExpiresAt <= now);
        await session.SaveChangesAsync(ct);
        foreach (var id in expired) connections.Revoke(id);
        return expired.Count;
    }
}
