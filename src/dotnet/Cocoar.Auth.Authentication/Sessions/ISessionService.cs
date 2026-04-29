using Cocoar.Auth.Authentication.Domain;
using ErrorOr;

namespace Cocoar.Auth.Authentication.Sessions;

public interface ISessionService
{
    /// <summary>Lists active (non-expired) sessions for a user.</summary>
    Task<ErrorOr<SessionListDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default);

    /// <summary>
    /// Records a new session row. Called from sign-in handlers after a
    /// successful login (password, MFA, OTP, magic-link, passkey, external).
    /// </summary>
    Task<ErrorOr<UserSession>> CreateSessionAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct = default);

    /// <summary>Revokes a single session owned by the caller.</summary>
    Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Revokes every session for a user; if <paramref name="exceptSessionId"/>
    /// is set, that one row is preserved (for "log out everywhere else").
    /// </summary>
    Task<ErrorOr<bool>> RevokeAllSessionsAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct = default);

    /// <summary>Updates the last-active timestamp.</summary>
    Task TouchSessionAsync(Guid sessionId, CancellationToken ct = default);
}
