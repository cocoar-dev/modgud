using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Service for recording login-related audit events.
/// </summary>
public interface ILoginAuditService
{
    /// <summary>
    /// Records a successful login event.
    /// </summary>
    Task RecordLoginAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed login event.
    /// </summary>
    Task RecordLoginFailedAsync(Guid userId, string? ipAddress, string? userAgent, LoginFailureReason reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a user lockout event.
    /// </summary>
    Task RecordLockoutAsync(Guid userId, DateTimeOffset? lockoutEnd, LockoutReason reason, CancellationToken cancellationToken = default);
}
