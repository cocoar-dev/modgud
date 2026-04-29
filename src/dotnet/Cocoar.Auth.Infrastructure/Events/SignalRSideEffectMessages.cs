using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;

namespace Cocoar.Auth.Infrastructure.Events;

public enum SignalRDispatchAction
{
    Created,
    Updated,
    Deleted
}

public record UserViewSignalRDispatch(SignalRDispatchAction Action, UserView? View, Guid Id);
