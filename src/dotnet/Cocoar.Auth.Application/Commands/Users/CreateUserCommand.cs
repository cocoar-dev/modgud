using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Users;

/// <summary>
/// Command to create a new user.
/// </summary>
public record CreateUserCommand(CreateUserDto Dto);

/// <summary>
/// Handler for CreateUserCommand.
/// </summary>
public class CreateUserHandler
{
    private readonly UserManager<ApplicationUser> _userManager;

    public CreateUserHandler(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<ErrorOr<UserDto>> HandleAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var dto = command.Dto;

        // Check for duplicate username
        var existingUser = await _userManager.FindByNameAsync(dto.UserName);
        if (existingUser is not null)
        {
            return UserErrors.DuplicateUserName(dto.UserName);
        }

        // Check for duplicate email if provided
        if (!string.IsNullOrWhiteSpace(dto.Email))
        {
            existingUser = await _userManager.FindByEmailAsync(dto.Email);
            if (existingUser is not null)
            {
                return UserErrors.DuplicateEmail(dto.Email);
            }
        }

        var user = new ApplicationUser(dto.UserName, dto.Email);
        user.SetPhoneNumber(dto.PhoneNumber);
        user.SetFirstName(dto.FirstName);
        user.SetLastName(dto.LastName);
        user.SetIsActive(dto.IsActive);
        user.SetLockoutEnabled(dto.LockoutEnabled);

        // Add roles
        foreach (var roleId in dto.Roles)
        {
            user.AddRole(roleId.Guid);
        }

        var result = await _userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
        {
            return UserErrors.CreationFailed(result.Errors.Select(e => e.Description));
        }

        return UserMapper.ToDto(user);
    }
}
