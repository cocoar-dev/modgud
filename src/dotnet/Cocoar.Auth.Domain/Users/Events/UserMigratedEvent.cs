using Cocoar.Auth.Domain.Common;

namespace Cocoar.Auth.Domain.Users.Events;

public record UserMigratedEvent(
    Guid Id,
    Optional<string> Firstname,
    Optional<string> Lastname,
    Optional<string> Acronym,
    Optional<string> Email,
    DateTime MigratedAt);
