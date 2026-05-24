using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Inbox;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;

namespace Cocoar.Auth.Infrastructure.Events;

public enum SignalRDispatchAction
{
    Created,
    Updated,
    Deleted
}

public record UserViewSignalRDispatch(SignalRDispatchAction Action, UserView? View, Guid Id);

/// <summary>
/// SignalR dispatch for the inbox-item view-projection. Carries the per-item
/// snapshot AND the recipient id so the hub can filter the per-event push to
/// just the owner of the item — every connected client subscribes to the
/// "InboxItem" subject, but only the recipient's stream sees the event.
/// </summary>
public record InboxItemSignalRDispatch(
    SignalRDispatchAction Action,
    InboxItemView? View,
    Guid Id,
    Guid RecipientUserId);
