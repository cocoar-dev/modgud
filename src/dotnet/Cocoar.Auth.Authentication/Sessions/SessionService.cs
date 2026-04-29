using Cocoar.Auth.Authentication.Domain;
using ErrorOr;
using Marten;

namespace Cocoar.Auth.Authentication.Sessions;

/// <summary>
/// Marten-backed <see cref="ISessionService"/>. Sessions are tenant-scoped
/// — the injected <see cref="IDocumentSession"/> resolves the active realm
/// via <c>TenantedSessionFactory</c>, so a user's sessions never leak across
/// realms.
/// </summary>
public class SessionService(IDocumentSession session, IDeviceInfoService deviceInfo) : ISessionService
{
    private static readonly TimeSpan DefaultSessionDuration = TimeSpan.FromDays(14);

    public async Task<ErrorOr<SessionListDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await session.Query<UserSession>()
            .Where(s => s.UserId == userId && s.ExpiresAt > now)
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
        var device = deviceInfo.Parse(userAgent);
        var entity = UserSession.Create(
            userId,
            sessionId: Guid.NewGuid().ToString(),
            ipAddress,
            userAgent,
            device.Browser,
            device.BrowserVersion,
            device.OperatingSystem,
            device.OsVersion,
            device.DeviceType,
            DefaultSessionDuration);

        session.Store(entity);
        await session.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<UserSession>(sessionId, ct);
        if (entity is null) return Error.NotFound("Session.NotFound", $"Session {sessionId} not found.");
        if (entity.UserId != userId) return Error.Forbidden("Session.NotOwner", "Caller does not own this session.");

        session.Delete<UserSession>(sessionId);
        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ErrorOr<bool>> RevokeAllSessionsAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct = default)
    {
        if (exceptSessionId.HasValue)
            session.DeleteWhere<UserSession>(s => s.UserId == userId && s.Id != exceptSessionId.Value);
        else
            session.DeleteWhere<UserSession>(s => s.UserId == userId);

        await session.SaveChangesAsync(ct);
        return true;
    }

    public async Task TouchSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var entity = await session.LoadAsync<UserSession>(sessionId, ct);
        if (entity is null) return;
        entity.Touch();
        session.Store(entity);
        await session.SaveChangesAsync(ct);
    }
}
