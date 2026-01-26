using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence.Repositories;

/// <summary>
/// Marten-based implementation of ISessionRepository.
/// </summary>
public class MartenSessionRepository : ISessionRepository
{
    private readonly IDocumentSession _session;

    public MartenSessionRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<UserSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await _session.LoadAsync<UserSession>(sessionId, cancellationToken);
    }

    public async Task<UserSession?> GetBySessionIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return await _session.Query<UserSession>()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
    }

    public async Task<List<UserSession>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await _session.Query<UserSession>()
            .Where(s => s.UserId == userId && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastActiveAt)
            .ToListAsync(cancellationToken);
        return sessions.ToList();
    }

    public async Task CreateAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        _session.Store(session);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        _session.Store(session);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _session.Delete<UserSession>(sessionId);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        _session.DeleteWhere<UserSession>(s => s.UserId == userId);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAllExceptAsync(Guid userId, Guid exceptSessionId, CancellationToken cancellationToken = default)
    {
        _session.DeleteWhere<UserSession>(s => s.UserId == userId && s.Id != exceptSessionId);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        _session.DeleteWhere<UserSession>(s => s.ExpiresAt <= now);
        await _session.SaveChangesAsync(cancellationToken);
    }
}
