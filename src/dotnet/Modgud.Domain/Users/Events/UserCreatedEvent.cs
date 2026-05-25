using Modgud.Domain.Common;

namespace Modgud.Domain.Users.Events;

public record UserCreatedEvent(Guid Id, Optional<string> Firstname, Optional<string> Lastname, Optional<string> Acronym, Optional<string> Email);
