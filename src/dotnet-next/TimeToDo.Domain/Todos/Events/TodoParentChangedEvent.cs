namespace TimeToDo.Domain.Todos.Events;

public record TodoParentChangedEvent(
    Guid Id,
    Guid? NewParentId,
    Guid? InheritedCustomerId,
    DateTime UpdatedAt);
