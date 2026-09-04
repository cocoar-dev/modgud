using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging;
using Modgud.Authentication.Events;
using Modgud.Domain.OAuth.Applications;

namespace Modgud.Authentication.BackChannelLogout;

/// <summary>
/// ADR 0009 transport A, step 1 — reacts to the forwarded <see cref="UserAccessEndedEvent"/>
/// and enqueues one durable <see cref="SendBackChannelLogout"/> per relying party that
/// (a) held tokens of the ended session, (b) did not initiate the logout itself and
/// (c) registered a back-channel logout URI. The user-facing request never waits for
/// any of this. Invoked by the Wolverine-driven Marten subscription over the event store
/// (strict order, durable progress); the envelope carries the tenant.
/// </summary>
public sealed class UserAccessEndedFanOutHandler(
    IDocumentSession session,
    BackChannelLogoutDispatchQueue dispatch,
    TimeProvider clock,
    ILogger<UserAccessEndedFanOutHandler> logger)
{
    public async Task Handle(IEvent<UserAccessEndedEvent> envelope, CancellationToken ct)
    {
        var @event = envelope.Data;
        if (@event.Targets.Count == 0) return;

        var realm = envelope.TenantId ?? session.TenantId;
        if (string.IsNullOrEmpty(realm))
        {
            logger.LogError("UserAccessEndedEvent for user {UserId} arrived without a tenant; no logout notifications sent", @event.UserId);
            return;
        }

        var now = clock.GetUtcNow();
        var pending = new List<BackChannelLogoutDelivery>();
        foreach (var target in @event.Targets)
        {
            if (string.Equals(target.ClientId, @event.InitiatingClientId, StringComparison.Ordinal))
                continue;

            var client = await session.Query<OAuthApplicationState>()
                .FirstOrDefaultAsync(a => a.ClientId == target.ClientId && !a.IsDeleted, ct);
            if (client is null
                || !client.Settings.TryGetValue(OAuthApplicationSettingKeys.BackChannelLogoutUri, out var uri)
                || string.IsNullOrWhiteSpace(uri))
                continue;

            pending.Add(new BackChannelLogoutDelivery
            {
                Id = Guid.CreateVersion7(),
                ClientId = target.ClientId,
                UserId = @event.UserId,
                SessionId = @event.Scope == AccessEndScope.Session ? @event.SessionId : null,
                Issuer = target.Issuer,
                Reason = @event.Reason,
                Scope = @event.Scope,
                Attempts = 0,
                NextAttemptAt = now,
                CreatedAt = now,
            });
        }
        if (pending.Count == 0) return;

        // The rows are the durable record (retried by the per-realm job); the dispatcher
        // makes the prompt first attempt off this subscription.
        session.Store(pending.ToArray());
        await session.SaveChangesAsync(ct);
        foreach (var delivery in pending)
            dispatch.Enqueue(realm, delivery.Id);
    }
}
