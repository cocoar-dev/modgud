namespace Cocoar.Auth.Application.DTOs.Auth;

/// <summary>
/// DTO for login request.
/// </summary>
public record LoginDto
{
    public required string UserName { get; init; }
    public required string Password { get; init; }
    public bool RememberMe { get; init; }
}

/// <summary>
/// DTO for login response.
/// </summary>
public record LoginResultDto
{
    public required bool Succeeded { get; init; }
    public bool RequiresTwoFactor { get; init; }
    public bool IsLockedOut { get; init; }
    public bool IsNotAllowed { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// DTO for current user info.
/// </summary>
public record CurrentUserDto
{
    public required string Id { get; init; }
    public required string UserName { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public List<string> Roles { get; init; } = [];
}
