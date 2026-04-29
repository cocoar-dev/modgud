using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using Cocoar.Auth.Infrastructure.Events;

namespace Cocoar.Auth.Api.Events;

public class SignalRProjectionDispatchHandler(DataEventDispatcher eventDispatcher)
{
    public void Handle(UserViewSignalRDispatch message)
    {
        Dispatch("User", message.Action, message.View, message.Id);
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
