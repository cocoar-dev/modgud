using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Users;

/// <summary>
/// Command to create a new user.
/// </summary>
public record CreateUserCommand(
    string UserName,
    string Password,
    string? Email,
    string? PhoneNumber,
    string? FirstName,
    string? LastName,
    bool IsActive = true,
    bool LockoutEnabled = true,
    List<ShortGuid>? Roles = null);

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

    public async Task<ErrorOr<ApplicationUser>> HandleAsync(CreateUserCommand command, CancellationToken cancellationToken)
    {
        // Check for duplicate username
        var existingUser = await _userManager.FindByNameAsync(command.UserName);
        if (existingUser is not null)
        {
            return UserErrors.DuplicateUserName(command.UserName);
        }

        // Check for duplicate email if provided
        if (!string.IsNullOrWhiteSpace(command.Email))
        {
            existingUser = await _userManager.FindByEmailAsync(command.Email);
            if (existingUser is not null)
            {
                return UserErrors.DuplicateEmail(command.Email);
            }
        }

        var user = new ApplicationUser(command.UserName, command.Email);
        user.SetPhoneNumber(command.PhoneNumber);
        user.SetFirstName(command.FirstName);
        user.SetLastName(command.LastName);
        user.SetIsActive(command.IsActive);
        user.SetLockoutEnabled(command.LockoutEnabled);

        // Add roles
        if (command.Roles is not null)
        {
            foreach (var roleId in command.Roles)
            {
                user.AddRole(roleId.Guid);
            }
        }

        var result = await _userManager.CreateAsync(user, command.Password);
        if (!result.Succeeded)
        {
            return UserErrors.CreationFailed(result.Errors.Select(e => e.Description));
        }

        return user;
    }
}
