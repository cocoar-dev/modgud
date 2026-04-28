using TimeToDo.Domain.Common;

namespace TimeToDo.Domain.Todos.Events;

public record TodoFlagsChangedEvent(
    Guid Id,
    Optional<bool> IsCritical,
    Optional<bool> IsAwaitingFeedback,
    DateTime UpdatedAt,
    Guid? UpdatedById);
