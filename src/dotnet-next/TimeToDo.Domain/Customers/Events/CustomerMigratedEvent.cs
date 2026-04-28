using TimeToDo.Domain.Common;

namespace TimeToDo.Domain.Customers.Events;

public record CustomerMigratedEvent(
    Guid Id,
    Optional<string> Name,
    Optional<bool> IsImportant,
    Optional<bool> IsArchived,
    DateTime MigratedAt);
