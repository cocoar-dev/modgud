namespace TimeToDo.Application.DTOs.User;

public class UserCreateDto
{
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Acronym { get; set; }
    public string? Email { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Password { get; set; }
}
