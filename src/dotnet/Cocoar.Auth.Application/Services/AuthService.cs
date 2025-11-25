using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;

namespace Cocoar.Auth.Application.Services;

public class AuthService
{
    private readonly IAuthenticationService _authenticationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IRoleRepository _roleRepository;
    private readonly IEmailSender _emailSender;

    public AuthService(
        IAuthenticationService authenticationService,
        UserManager<ApplicationUser> userManager,
        IRoleRepository roleRepository,
        IEmailSender emailSender)
    {
        _authenticationService = authenticationService;
        _userManager = userManager;
        _roleRepository = roleRepository;
        _emailSender = emailSender;
    }

    public async Task<ErrorOr<LoginResultDto>> LoginAsync(LoginDto dto, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByNameAsync(dto.UserName);
        if (user is null)
        {
            return new LoginResultDto
            {
                Succeeded = false,
                ErrorMessage = $"User not found for username: {dto.UserName}"
            };
        }

        // Debug: Check if user has password hash
        var hasPassword = !string.IsNullOrEmpty(user.PasswordHash);

        if (!user.IsActive)
        {
            return new LoginResultDto
            {
                Succeeded = false,
                IsNotAllowed = true,
                ErrorMessage = "This account is not active."
            };
        }

        var result = await _authenticationService.PasswordSignInAsync(
            user,
            dto.Password,
            dto.RememberMe,
            lockoutOnFailure: true,
            cancellationToken);

        if (result.Succeeded)
        {
            return new LoginResultDto { Succeeded = true };
        }

        if (result.RequiresTwoFactor)
        {
            return new LoginResultDto
            {
                Succeeded = false,
                RequiresTwoFactor = true,
                ErrorMessage = "Two-factor authentication is required."
            };
        }

        if (result.IsLockedOut)
        {
            return new LoginResultDto
            {
                Succeeded = false,
                IsLockedOut = true,
                ErrorMessage = "This account has been locked out. Please try again later."
            };
        }

        if (result.IsNotAllowed)
        {
            return new LoginResultDto
            {
                Succeeded = false,
                IsNotAllowed = true,
                ErrorMessage = "This account is not allowed to sign in."
            };
        }

        return new LoginResultDto
        {
            Succeeded = false,
            ErrorMessage = $"Invalid username or password. HasPassword: {hasPassword}, IsLockedOut: {result.IsLockedOut}, IsNotAllowed: {result.IsNotAllowed}"
        };
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _authenticationService.SignOutAsync(cancellationToken);
    }

    public async Task<ErrorOr<CurrentUserDto>> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        var roleNames = new List<string>();
        if (user.Roles.Count > 0)
        {
            var roles = await _roleRepository.GetByIdsAsync(user.Roles, cancellationToken);
            roleNames = roles.Select(r => r.Name).ToList();
        }

        return new CurrentUserDto
        {
            Id = user.Id.ToString(),
            UserName = user.UserName,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Roles = roleNames
        };
    }

    public async Task<ErrorOr<RegisterResultDto>> RegisterAsync(RegisterDto dto, string baseUrl, CancellationToken cancellationToken = default)
    {
        // Check for duplicate username
        var existingUser = await _userManager.FindByNameAsync(dto.UserName);
        if (existingUser is not null)
        {
            return UserErrors.DuplicateUserName(dto.UserName);
        }

        // Check for duplicate email
        existingUser = await _userManager.FindByEmailAsync(dto.Email);
        if (existingUser is not null)
        {
            return UserErrors.DuplicateEmail(dto.Email);
        }

        var user = new ApplicationUser(dto.UserName, dto.Email);
        user.SetFirstName(dto.FirstName);
        user.SetLastName(dto.LastName);
        user.SetIsActive(true);

        var result = await _userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            return new RegisterResultDto
            {
                Succeeded = false,
                Errors = result.Errors.Select(e => e.Description).ToList()
            };
        }

        // Generate email confirmation token and send email
        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var confirmationLink = $"{baseUrl}/api/auth/confirm-email?userId={user.Id}&token={encodedToken}";

        await _emailSender.SendEmailConfirmationAsync(
            user.Email!,
            user.UserName!,
            confirmationLink,
            cancellationToken);

        return new RegisterResultDto
        {
            Succeeded = true,
            UserId = user.Id.ToString(),
            RequiresEmailConfirmation = true
        };
    }

    public async Task<ErrorOr<bool>> ConfirmEmailAsync(ConfirmEmailDto dto, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(dto.UserId, out var userId))
        {
            return AuthErrors.UserNotFound;
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        if (user.EmailConfirmed)
        {
            return AuthErrors.EmailAlreadyConfirmed;
        }

        // Decode the token
        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(dto.Token));
        }
        catch
        {
            return AuthErrors.InvalidEmailConfirmationToken;
        }

        var result = await _userManager.ConfirmEmailAsync(user, decodedToken);
        if (!result.Succeeded)
        {
            return AuthErrors.InvalidEmailConfirmationToken;
        }

        return true;
    }

    public async Task<ErrorOr<bool>> ResendConfirmationEmailAsync(ResendConfirmationDto dto, string baseUrl, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is null)
        {
            // Don't reveal that the user doesn't exist
            return true;
        }

        if (user.EmailConfirmed)
        {
            // Don't reveal that the email is already confirmed
            return true;
        }

        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var confirmationLink = $"{baseUrl}/api/auth/confirm-email?userId={user.Id}&token={encodedToken}";

        await _emailSender.SendEmailConfirmationAsync(
            user.Email!,
            user.UserName!,
            confirmationLink,
            cancellationToken);

        return true;
    }

    public async Task<ErrorOr<bool>> ForgotPasswordAsync(ForgotPasswordDto dto, string baseUrl, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is null)
        {
            // Don't reveal that the user doesn't exist
            return true;
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var resetLink = $"{baseUrl}/reset-password?email={Uri.EscapeDataString(dto.Email)}&token={encodedToken}";

        await _emailSender.SendPasswordResetAsync(
            user.Email!,
            user.UserName!,
            resetLink,
            cancellationToken);

        return true;
    }

    public async Task<ErrorOr<bool>> ResetPasswordAsync(ResetPasswordRequestDto dto, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(dto.Email);
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        // Decode the token
        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(dto.Token));
        }
        catch
        {
            return AuthErrors.InvalidPasswordResetToken;
        }

        var result = await _userManager.ResetPasswordAsync(user, decodedToken, dto.NewPassword);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == "InvalidToken"))
            {
                return AuthErrors.InvalidPasswordResetToken;
            }
            return AuthErrors.PasswordResetFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }

    public async Task<ErrorOr<ProfileDto>> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        return new ProfileDto
        {
            Id = user.Id.ToString(),
            UserName = user.UserName!,
            Email = user.Email,
            EmailConfirmed = user.EmailConfirmed,
            FirstName = user.FirstName,
            LastName = user.LastName,
            PhoneNumber = user.PhoneNumber,
            PhoneNumberConfirmed = user.PhoneNumberConfirmed,
            TwoFactorEnabled = user.TwoFactorEnabled,
            CreatedAt = user.CreatedAt
        };
    }

    public async Task<ErrorOr<ProfileDto>> UpdateProfileAsync(Guid userId, UpdateProfileDto dto, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.UserNotFound;
        }

        if (dto.FirstName is not null)
            user.SetFirstName(dto.FirstName);

        if (dto.LastName is not null)
            user.SetLastName(dto.LastName);

        if (dto.PhoneNumber is not null)
            user.SetPhoneNumber(dto.PhoneNumber);

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return UserErrors.UpdateFailed(result.Errors.Select(e => e.Description));
        }

        return new ProfileDto
        {
            Id = user.Id.ToString(),
            UserName = user.UserName!,
            Email = user.Email,
            EmailConfirmed = user.EmailConfirmed,
            FirstName = user.FirstName,
            LastName = user.LastName,
            PhoneNumber = user.PhoneNumber,
            PhoneNumberConfirmed = user.PhoneNumberConfirmed,
            TwoFactorEnabled = user.TwoFactorEnabled,
            CreatedAt = user.CreatedAt
        };
    }
}
