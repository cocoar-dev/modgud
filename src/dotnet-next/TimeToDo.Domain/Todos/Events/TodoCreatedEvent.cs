using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Domain.Todos.Events;

public record TodoCreatedEvent(
    Guid Id,
    string Title,
    string? Description,
    DateTime? DueDate,
    TodoStatus Status,
    Guid? CustomerId,
    List<Guid> ResponsibleUserIds,
    Guid? ParentTodoId,
    bool IsCritical,
    bool IsAwaitingFeedback,
    DateTime CreatedAt,
    Guid CreatedById);
