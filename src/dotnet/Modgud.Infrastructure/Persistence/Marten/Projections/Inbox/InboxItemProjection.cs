using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Modgud.Application.Inbox.Events;
using Modgud.Infrastructure.Events;

namespace Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

/// <summary>
/// Async single-stream projection — one stream per inbox item. RaiseSideEffects
/// fires a SignalR dispatch so the recipient's connected clients see the new
/// item live (no polling). The hub filters by RecipientUserId so only the
/// owner of the item receives the push.
///
/// <para>partial because Marten 9's source-generator (JasperFx.Events.SourceGenerator)
/// emits the apply-dispatcher into this class; without partial the boot throws
/// <c>InvalidProjectionException</c> — see
/// <c>dev-docs/engineering-gotchas/marten-raise-side-effects.md</c> for the wider context.</para>
/// </summary>
public partial class InboxItemProjection : SingleStreamProjection<InboxItemView, Guid>
{
    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<InboxItemView> slice)
    {
        var snapshot = slice.Snapshot;
        if (snapshot is null || !ProjectionSideEffects.Enabled)
            return ValueTask.CompletedTask;

        var action = slice.Events().Any(e => e.Data is InboxItemCreatedEvent)
            ? SignalRDispatchAction.Created
            : SignalRDispatchAction.Updated;

        slice.PublishMessage(new InboxItemSignalRDispatch(action, snapshot, snapshot.Id, snapshot.RecipientUserId));

        return ValueTask.CompletedTask;
    }

    public InboxItemView Create(InboxItemCreatedEvent @event)
    {
        return new InboxItemView
        {
            Id = @event.Id,
            RecipientUserId = @event.RecipientUserId,
            Kind = @event.Kind,
            Severity = @event.Severity,
            TitleKey = @event.TitleKey,
            BodyKey = @event.BodyKey,
            Params = @event.Params,
            Link = @event.Link,
            SourceType = @event.SourceType,
            SourceId = @event.SourceId,
            CreatedAt = @event.CreatedAt,
        };
    }

    public InboxItemView Apply(InboxItemReadEvent @event, InboxItemView current)
    {
        return current with { ReadAt = @event.ReadAt };
    }

    public InboxItemView Apply(InboxItemDismissedEvent @event, InboxItemView current)
    {
        return current with { DismissedAt = @event.DismissedAt };
    }

    public InboxItemView Apply(InboxItemSnoozedEvent @event, InboxItemView current)
    {
        return current with { SnoozeUntil = @event.SnoozeUntil };
    }
}
