using TimeToDo.Domain.Common;

namespace TimeToDo.Application.DTOs.User;

public class UserUpdateDto
{
    public Optional<string> Firstname { get; set; }
    public Optional<string> Lastname { get; set; }
    public Optional<string> Acronym { get; set; }
    public Optional<string> Email { get; set; }
    public Optional<string> UserName { get; set; }

    public Optional<bool> IsActive { get; set; }
}
