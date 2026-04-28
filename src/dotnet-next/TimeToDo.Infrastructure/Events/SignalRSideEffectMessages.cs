using TimeToDo.Infrastructure.Persistence.Marten.Projections.Comments;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Customers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Todos;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;

namespace TimeToDo.Infrastructure.Events;

public enum SignalRDispatchAction
{
    Created,
    Updated,
    Deleted
}

public record UserViewSignalRDispatch(SignalRDispatchAction Action, UserView? View, Guid Id);

public record CustomerViewSignalRDispatch(SignalRDispatchAction Action, CustomerView? View, Guid Id);

public record TodoViewSignalRDispatch(SignalRDispatchAction Action, TodoView? View, Guid Id);

public record CommentViewSignalRDispatch(SignalRDispatchAction Action, CommentView? View, Guid Id);
