using Cocoar.Auth.Application.DTOs.Auth;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Services;

public class AuthService
{
    private readonly IAuthenticationService _authenticationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IRoleRepository _roleRepository;

    public AuthService(
        IAuthenticationService authenticationService,
        UserManager<ApplicationUser> userManager,
        IRoleRepository roleRepository)
    {
        _authenticationService = authenticationService;
        _userManager = userManager;
        _roleRepository = roleRepository;
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
}
