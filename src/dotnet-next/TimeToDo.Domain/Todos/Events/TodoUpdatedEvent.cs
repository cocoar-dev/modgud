using TimeToDo.Domain.Common;
using TimeToDo.Domain.ValueObjects;

namespace TimeToDo.Domain.Todos.Events;

public record TodoUpdatedEvent(
    Guid Id,
    Optional<string> Title,
    Optional<string?> Description,
    Optional<DateTime?> DueDate,
    Optional<TodoStatus> Status,
    Optional<Guid?> CustomerId,
    Optional<List<Guid>> ResponsibleUserIds,
    Optional<bool> IsCritical,
    Optional<bool> IsAwaitingFeedback,
    DateTime UpdatedAt,
    Guid UpdatedById);
