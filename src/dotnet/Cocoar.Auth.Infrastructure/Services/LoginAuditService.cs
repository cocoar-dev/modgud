using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Aggregates;
using Cocoar.Auth.Domain.Events;
using Marten;

namespace Cocoar.Auth.Infrastructure.Services;

/// <summary>
/// Service for recording login-related audit events in the event store.
/// </summary>
public class LoginAuditService : ILoginAuditService
{
    private readonly IDocumentSession _session;

    public LoginAuditService(IDocumentSession session)
    {
        _session = session;
    }

    public async Task RecordLoginAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken cancellationToken = default)
    {
        _session.Events.Append(userId, new UserLoggedIn(userId, ipAddress, userAgent));
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordLoginFailedAsync(Guid userId, string? ipAddress, string? userAgent, LoginFailureReason reason, CancellationToken cancellationToken = default)
    {
        _session.Events.Append(userId, new UserLoginFailed(userId, ipAddress, userAgent, reason));
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordLockoutAsync(Guid userId, DateTimeOffset? lockoutEnd, LockoutReason reason, CancellationToken cancellationToken = default)
    {
        _session.Events.Append(userId, new UserLockedOut(userId, lockoutEnd, reason));
        await _session.SaveChangesAsync(cancellationToken);
    }
}
