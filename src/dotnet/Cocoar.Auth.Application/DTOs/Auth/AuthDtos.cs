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
    public Guid? UserId { get; init; }
    public bool RequiresTwoFactor { get; init; }
    public bool IsLockedOut { get; init; }
    public bool IsNotAllowed { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Available 2FA methods when RequiresTwoFactor is true.
    /// Possible values: "totp", "email", "webauthn", "recovery"
    /// </summary>
    public List<string>? AvailableTwoFactorMethods { get; init; }
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

/// <summary>
/// DTO for user registration request.
/// </summary>
public record RegisterDto
{
    public required string UserName { get; init; }
    public required string Email { get; init; }
    public required string Password { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
}

/// <summary>
/// DTO for registration result.
/// </summary>
public record RegisterResultDto
{
    public required bool Succeeded { get; init; }
    public string? UserId { get; init; }
    public bool RequiresEmailConfirmation { get; init; }
    public List<string> Errors { get; init; } = [];
}

/// <summary>
/// DTO for email confirmation request.
/// </summary>
public record ConfirmEmailDto
{
    public required string UserId { get; init; }
    public required string Token { get; init; }
}

/// <summary>
/// DTO for resending email confirmation.
/// </summary>
public record ResendConfirmationDto
{
    public required string Email { get; init; }
}

/// <summary>
/// DTO for forgot password request.
/// </summary>
public record ForgotPasswordDto
{
    public required string Email { get; init; }
}

/// <summary>
/// DTO for reset password request.
/// </summary>
public record ResetPasswordRequestDto
{
    public required string Email { get; init; }
    public required string Token { get; init; }
    public required string NewPassword { get; init; }
}

/// <summary>
/// DTO for profile update request.
/// </summary>
public record UpdateProfileDto
{
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? PhoneNumber { get; init; }
}

/// <summary>
/// DTO for profile response.
/// </summary>
public record ProfileDto
{
    public required string Id { get; init; }
    public required string UserName { get; init; }
    public string? Email { get; init; }
    public bool EmailConfirmed { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? PhoneNumber { get; init; }
    public bool PhoneNumberConfirmed { get; init; }
    public bool TwoFactorEnabled { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
