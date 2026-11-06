using Marten;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace Modgud.Infrastructure.FollowUps;

/// <summary>
/// Publishes <see cref="IFollowUpMessage"/>s inside the transaction of a realm's Marten session
/// (the transactional outbox). The message is written in the same commit as the state change;
/// nothing is sent when the session is never committed, and the message is handed to its
/// handler only after the commit.
/// </summary>
public interface IFollowUpOutbox
{
    /// <summary>Queues <paramref name="message"/> on <paramref name="session"/>; it is written
    /// by that session's next <c>SaveChangesAsync</c>.</summary>
    ValueTask PublishAsync<T>(IDocumentSession session, T message) where T : IFollowUpMessage;
}

/// <summary>
/// One Wolverine outbox per Marten session. The outbox carries the session's tenant: a plain
/// endpoint's bus gets its tenant from <c>TenantContextMiddleware</c>, but an outbox is a bus of
/// its own, and without the tenant the handler could not open the realm's session later.
/// </summary>
internal sealed class FollowUpOutbox(IWolverineRuntime runtime) : IFollowUpOutbox
{
    private readonly Dictionary<IDocumentSession, MartenOutbox> _outboxes = new(ReferenceEqualityComparer.Instance);

    public ValueTask PublishAsync<T>(IDocumentSession session, T message) where T : IFollowUpMessage
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrEmpty(session.TenantId))
            throw new InvalidOperationException("A follow-up needs a realm session.");

        if (!_outboxes.TryGetValue(session, out var outbox))
        {
            outbox = new MartenOutbox(runtime, session) { TenantId = session.TenantId };
            _outboxes[session] = outbox;
        }

        return outbox.PublishAsync(message);
    }
}
