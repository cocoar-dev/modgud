using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;

namespace Cocoar.Auth.Application.DTOs.Users;

/// <summary>
/// DTO for returning user information.
/// </summary>
public record UserDto
{
    public required ShortGuid Id { get; init; }
    public required string UserName { get; init; }
    public string? Email { get; init; }
    public bool EmailConfirmed { get; init; }
    public string? PhoneNumber { get; init; }
    public bool PhoneNumberConfirmed { get; init; }
    public bool TwoFactorEnabled { get; init; }
    public DateTimeOffset? LockoutEnd { get; init; }
    public bool LockoutEnabled { get; init; }
    public int AccessFailedCount { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public List<ShortGuid> Roles { get; init; } = [];
    public List<UserClaimDto> Claims { get; init; } = [];
}

/// <summary>
/// DTO for a user claim.
/// </summary>
public record UserClaimDto
{
    public required string Type { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// DTO for creating a new user.
/// </summary>
public record CreateUserDto
{
    public required string UserName { get; init; }
    public required string Password { get; init; }
    public string? Email { get; init; }
    public string? PhoneNumber { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; } = true;
    public bool LockoutEnabled { get; init; } = true;
    public List<ShortGuid> Roles { get; init; } = [];
    public List<UserClaimDto> Claims { get; init; } = [];
}

/// <summary>
/// DTO for updating a user. Uses Optional to distinguish between "not set" and "set to null".
/// </summary>
public record UpdateUserDto
{
    public Optional<string> UserName { get; init; }
    public Optional<string?> Email { get; init; }
    public Optional<string?> PhoneNumber { get; init; }
    public Optional<string?> FirstName { get; init; }
    public Optional<string?> LastName { get; init; }
    public Optional<DateTimeOffset?> ExpiresAt { get; init; }
    public Optional<bool> IsActive { get; init; }
    public Optional<bool> LockoutEnabled { get; init; }
    public Optional<bool> EmailConfirmed { get; init; }
    public Optional<bool> PhoneNumberConfirmed { get; init; }
    public Optional<bool> TwoFactorEnabled { get; init; }
    public Optional<List<ShortGuid>> Roles { get; init; }
    public Optional<List<UserClaimDto>> Claims { get; init; }
}

/// <summary>
/// DTO for a list of users with pagination.
/// </summary>
public record UserListDto
{
    public required List<UserDto> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
}

/// <summary>
/// DTO for resetting a user's password (admin action).
/// </summary>
public record ResetPasswordDto
{
    public required string NewPassword { get; init; }
}

/// <summary>
/// DTO for changing your own password.
/// </summary>
public record ChangePasswordDto
{
    public required string CurrentPassword { get; init; }
    public required string NewPassword { get; init; }
}
