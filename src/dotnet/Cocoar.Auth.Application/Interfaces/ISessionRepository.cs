using Cocoar.Auth.Domain.Entities;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository interface for user session operations.
/// </summary>
public interface ISessionRepository
{
    /// <summary>
    /// Gets a session by ID.
    /// </summary>
    Task<UserSession?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a session by session ID (cookie identifier).
    /// </summary>
    Task<UserSession?> GetBySessionIdAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active sessions for a user.
    /// </summary>
    Task<List<UserSession>> GetByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new session.
    /// </summary>
    Task CreateAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing session.
    /// </summary>
    Task UpdateAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a session.
    /// </summary>
    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all sessions for a user.
    /// </summary>
    Task DeleteAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all sessions for a user except the specified one.
    /// </summary>
    Task DeleteAllExceptAsync(Guid userId, Guid exceptSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes expired sessions.
    /// </summary>
    Task DeleteExpiredAsync(CancellationToken cancellationToken = default);
}
