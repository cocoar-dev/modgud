namespace Cocoar.Auth.Application.DTOs.User;

public class UserCreateDto
{
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Acronym { get; set; }
    public string? Email { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Password { get; set; }
    /// <summary>
    /// Admin opt-in to mark the Identity EmailConfirmed flag at creation —
    /// skips the magic-link verify step for internal/trusted users.
    /// </summary>
    public bool EmailConfirmed { get; set; }
}
