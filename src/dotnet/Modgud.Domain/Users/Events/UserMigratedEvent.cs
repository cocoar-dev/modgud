using Modgud.Domain.Common;

namespace Modgud.Domain.Users.Events;

public record UserMigratedEvent(
    Guid Id,
    Optional<string> Firstname,
    Optional<string> Lastname,
    Optional<string> Acronym,
    Optional<string> Email,
    DateTime MigratedAt);
