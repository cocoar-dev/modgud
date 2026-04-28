using TimeToDo.Domain.Common;

namespace TimeToDo.Domain.Customers.Events;

public record CustomerUpdatedEvent(
    Guid Id,
    Optional<string> Name,
    Optional<bool> IsImportant,
    Optional<bool> IsArchived);
