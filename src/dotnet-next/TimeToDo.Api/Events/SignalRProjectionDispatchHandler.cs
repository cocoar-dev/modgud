using BuildingBlocks.EventDispatcher;
using BuildingBlocks.Helper;
using TimeToDo.Infrastructure.Events;

namespace TimeToDo.Api.Events;

public class SignalRProjectionDispatchHandler(DataEventDispatcher eventDispatcher)
{
    public void Handle(UserViewSignalRDispatch message)
    {
        Dispatch("User", message.Action, message.View, message.Id);
    }

    public void Handle(CustomerViewSignalRDispatch message)
    {
        Dispatch("Customer", message.Action, message.View, message.Id);
    }

    public void Handle(TodoViewSignalRDispatch message)
    {
        Dispatch("Todo", message.Action, message.View, message.Id);
    }

    public void Handle(CommentViewSignalRDispatch message)
    {
        Dispatch("Comment", message.Action, message.View, message.Id);
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
