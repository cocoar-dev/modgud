using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Gdpr;
using Modgud.Infrastructure.Events;
using Modgud.Infrastructure.Persistence.Marten.Mappers;

namespace Modgud.Api.Events;

public class SignalRProjectionDispatchHandler(DataEventDispatcher eventDispatcher, IDocumentSession session)
{
    public async Task Handle(UserViewSignalRDispatch message)
    {
        // Enrich the view-snapshot with the fields that live on documents OTHER
        // than the event-sourced UserView stream, so the SignalR payload matches
        // the DTO admin clients fetch on initial load (otherwise a live push
        // would silently reset them): EmailConfirmed (ApplicationUser, Identity-
        // side) and the pending-deletion state (UserDeletionState). Without the
        // latter, binning/restoring a user would push a row with no lifecycle
        // badge until the next full reload.
        object? payload = message.View;
        if (message.Action != SignalRDispatchAction.Deleted && message.View is not null)
        {
            var dto = message.View.ToDto();
            var appUser = await session.LoadAsync<ApplicationUser>(message.Id);
            dto.EmailConfirmed = appUser?.EmailConfirmed ?? false;

            var deletion = await session.LoadAsync<UserDeletionState>(message.Id);
            if (deletion?.IsDeletionPending == true)
            {
                dto.IsDeletionPending = true;
                dto.DeletionInitiator = deletion.DeletionInitiator?.ToString();
                dto.DeletionDeadline = deletion.DeletionConfirmationDeadline;
            }
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
        // The handler runs in the originating realm's context — the outboxed
        // session is opened for the message's tenant — so session.TenantId is
        // the authoritative realm. Stamp it so consumers scope delivery to the
        // matching connection and never leak across realms.
        var tenant = session.TenantId;

        switch (action)
        {
            case SignalRDispatchAction.Created:
                eventDispatcher.DispatchCreatedEvent(subject, view, tenant);
                break;

            case SignalRDispatchAction.Updated:
                eventDispatcher.DispatchUpdatedEvent(subject, view, tenant);
                break;

            case SignalRDispatchAction.Deleted:
                eventDispatcher.DispatchDeletedEvent(subject, new ShortGuid(id).ToString(), tenant);
                break;

            default:
                eventDispatcher.DispatchUpdatedEvent(subject, view, tenant);
                break;
        }
    }
}
