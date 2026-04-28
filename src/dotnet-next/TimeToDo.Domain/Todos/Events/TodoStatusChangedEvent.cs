using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Domain.Todos.Events;

public record TodoStatusChangedEvent(
    Guid Id,
    TodoStatus Status,
    DateTime UpdatedAt,
    Guid? UpdatedById);
