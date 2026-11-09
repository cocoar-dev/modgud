using Microsoft.Extensions.DependencyInjection;
using Modgud.Infrastructure.FollowUps;
using Modgud.Infrastructure.Persistence.Tenancy;
using Wolverine;

namespace Modgud.Authentication.Sessions;

/// <summary>
/// B4-AP-01 — a user's live access is owed a revoke by a change that is already decided (an email
/// change: the email is the account-recovery anchor). Written in the SAME commit as that change
/// through <see cref="IFollowUpOutbox"/>, so a failing revoke after the commit is retried instead of
/// lost. Every step of <see cref="IUserAccessRevoker.RevokeAllAccessAsync"/> is safe to repeat.
/// </summary>
public sealed record RevokeUserAccess(Guid UserId, AccessRevocationReason Reason) : IFollowUpMessage;

/// <summary>
/// Runs <see cref="RevokeUserAccess"/> on the durable <c>follow-ups</c> queue; a failure is retried on
/// its schedule and finally dead-lettered, where the readiness check reports it. The revoker's
/// stores open their sessions through the tenanted session factory, so the handler enters the
/// realm the outbox stamped on the envelope.
/// </summary>
public static class RevokeUserAccessHandler
{
    public static async Task Handle(
        RevokeUserAccess message, Envelope envelope, IServiceScopeFactory scopes, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(envelope.TenantId))
            throw new FollowUpPermanentException($"Access revoke for user '{message.UserId}' arrived without a realm.");

        // A safety net of the immediate revoke: never next to it on the same user's stream.
        await FollowUpTiming.WaitUntilDueAsync(envelope, ct);
        using var realm = TenantContext.Enter(envelope.TenantId);
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUserAccessRevoker>()
            .RevokeAllAccessAsync(message.UserId, message.Reason, ct);
    }
}
