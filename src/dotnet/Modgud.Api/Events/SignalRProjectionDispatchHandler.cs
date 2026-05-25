using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Events;
using Modgud.Infrastructure.Persistence.Marten.Mappers;

namespace Modgud.Api.Events;

public class SignalRProjectionDispatchHandler(DataEventDispatcher eventDispatcher, IDocumentSession session)
{
    public async Task Handle(UserViewSignalRDispatch message)
    {
        // Enrich the view-snapshot with EmailConfirmed from the ApplicationUser
        // doc (Identity-side, not tracked by the projection) so the SignalR
        // payload matches the DTO admin clients fetched on initial load.
        object? payload = message.View;
        if (message.Action != SignalRDispatchAction.Deleted && message.View is not null)
        {
            var dto = message.View.ToDto();
            var appUser = await session.LoadAsync<ApplicationUser>(message.Id);
            dto.EmailConfirmed = appUser?.EmailConfirmed ?? false;
            payload = dto;
        }
        Dispatch("User", message.Action, payload, message.Id);
    }

    public Task Handle(InboxItemSignalRDispatch message)
    {
        // We pass the raw InboxItemView through the dispatcher — the InboxHub
        // is the place that knows about per-recipient filtering and DTO shape
        // (it does `view.RecipientUserId == ctx.UserId` first, then ToDto()).
        // Inbox items never delete; Dismiss is just another Update event.
        Dispatch("InboxItem", message.Action, message.View, message.Id);
        return Task.CompletedTask;
    }

    private void Dispatch(string subject, SignalRDispatchAction action, object? view, Guid id)
    {
        switch (action)
        {
            case SignalRDispatchAction.Created:
                eventDispatcher.DispatchCreatedEvent(subject, view);
                break;

            case SignalRDispatchAction.Updated:
                eventDispatcher.DispatchUpdatedEvent(subject, view);
                break;

            case SignalRDispatchAction.Deleted:
                eventDispatcher.DispatchDeletedEvent(subject, new ShortGuid(id).ToString());
                break;

            default:
                eventDispatcher.DispatchUpdatedEvent(subject, view);
                break;
        }
    }
}
