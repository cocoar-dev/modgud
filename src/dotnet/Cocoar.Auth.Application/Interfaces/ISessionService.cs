using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for managing user sessions.
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Gets all active sessions for a user.
    /// </summary>
    Task<ErrorOr<SessionListDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new session for a user.
    /// </summary>
    Task<ErrorOr<UserSession>> CreateSessionAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a specific session.
    /// </summary>
    Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes all sessions for a user except the current one.
    /// </summary>
    Task<ErrorOr<bool>> RevokeAllSessionsAsync(Guid userId, Guid? exceptSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the last active time for a session.
    /// </summary>
    Task TouchSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
