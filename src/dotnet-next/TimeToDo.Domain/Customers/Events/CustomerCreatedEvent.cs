using TimeToDo.Domain.Common;

namespace TimeToDo.Domain.Customers.Events;

public record CustomerCreatedEvent(
    Guid Id,
    Optional<string> Name,
    Optional<bool> IsImportant);
