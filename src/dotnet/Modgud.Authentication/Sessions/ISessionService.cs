using Modgud.Authentication.Domain;
using ErrorOr;
using Modgud.Domain.Realms;

namespace Modgud.Authentication.Sessions;

public interface ISessionService
{
    /// <summary>Lists active (non-expired) sessions for a user.</summary>
    Task<ErrorOr<SessionListDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default);

    /// <summary>
    /// Records a new session row. Called from sign-in handlers after a
    /// successful login (password, MFA, OTP, magic-link, passkey, external).
    /// </summary>
    Task<ErrorOr<UserSession>> CreateSessionAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken ct = default);

    Task<BrowserSessionPolicy> GetPolicyAsync(CancellationToken ct = default);

    /// <summary>Loads and validates the authoritative session. Successful
    /// validation also performs a throttled sliding-idle touch.</summary>
    Task<UserSession?> ValidateSessionAsync(Guid userId, Guid sessionId, bool touch, CancellationToken ct = default);

    /// <summary>Revokes a single session owned by the caller.</summary>
    Task<ErrorOr<bool>> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>ADR 0009 — ends one browser session with an explicit reason
    /// (<see cref="Modgud.Authentication.Events.AccessEndReasons"/>) and, for an
    /// RP-initiated logout, the client that asked for it (which is then not notified).
    /// <see cref="RevokeSessionAsync"/> is this with reason <c>revoked</c>.</summary>
    Task<ErrorOr<bool>> EndSessionAsync(Guid userId, Guid sessionId, string reason, string? initiatingClientId, CancellationToken ct = default);

    /// <summary>
    /// Revokes every session for a user; if <paramref name="exceptSessionId"/>
    /// is set, that one row is preserved (for "log out everywhere else").
    /// </summary>
    Task<ErrorOr<bool>> RevokeAllSessionsAsync(Guid userId, Guid? exceptSessionId, CancellationToken ct = default);

    /// <summary>Updates the last-active timestamp.</summary>
    Task TouchSessionAsync(Guid sessionId, CancellationToken ct = default);

    Task<int> PruneExpiredAsync(CancellationToken ct = default);
}
