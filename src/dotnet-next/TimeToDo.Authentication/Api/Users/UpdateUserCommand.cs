using TimeToDo.Domain.Common;

namespace TimeToDo.Authentication.Api.Users;

public record UpdateUserCommand(Guid UserId, Optional<string> Firstname, Optional<string> Lastname, Optional<string> Acronym, Optional<string> Email, Optional<string> UserName);
