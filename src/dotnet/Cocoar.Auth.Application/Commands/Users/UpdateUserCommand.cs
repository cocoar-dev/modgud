using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Users;

/// <summary>
/// Command to update an existing user.
/// </summary>
public record UpdateUserCommand(
    ShortGuid Id,
    Optional<string> UserName,
    Optional<string?> Email,
    Optional<string?> PhoneNumber,
    Optional<string?> FirstName,
    Optional<string?> LastName,
    Optional<bool> IsActive,
    Optional<bool> LockoutEnabled,
    Optional<bool> EmailConfirmed,
    Optional<bool> PhoneNumberConfirmed,
    Optional<bool> TwoFactorEnabled,
    Optional<List<ShortGuid>> Roles);

/// <summary>
/// Handler for UpdateUserCommand.
/// </summary>
public class UpdateUserHandler
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserRepository _userRepository;

    public UpdateUserHandler(
        UserManager<ApplicationUser> userManager,
        IUserRepository userRepository)
    {
        _userManager = userManager;
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<ApplicationUser>> HandleAsync(UpdateUserCommand command, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(command.Id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(command.Id.Guid);
        }

        if (command.UserName.HasValue)
        {
            var newUserName = command.UserName.Value!;
            var existingUser = await _userManager.FindByNameAsync(newUserName);
            if (existingUser is not null && existingUser.Id != user.Id)
            {
                return UserErrors.DuplicateUserName(newUserName);
            }
            user.SetUserName(newUserName);
        }

        if (command.Email.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(command.Email.Value))
            {
                var existingUser = await _userManager.FindByEmailAsync(command.Email.Value);
                if (existingUser is not null && existingUser.Id != user.Id)
                {
                    return UserErrors.DuplicateEmail(command.Email.Value);
                }
            }
            user.SetEmail(command.Email.Value);
        }

        if (command.PhoneNumber.HasValue)
            user.SetPhoneNumber(command.PhoneNumber.Value);

        if (command.FirstName.HasValue)
            user.SetFirstName(command.FirstName.Value);

        if (command.LastName.HasValue)
            user.SetLastName(command.LastName.Value);

        if (command.IsActive.HasValue)
            user.SetIsActive(command.IsActive.Value);

        if (command.LockoutEnabled.HasValue)
            user.SetLockoutEnabled(command.LockoutEnabled.Value);

        if (command.EmailConfirmed.HasValue)
            user.SetEmailConfirmed(command.EmailConfirmed.Value);

        if (command.PhoneNumberConfirmed.HasValue)
            user.SetPhoneNumberConfirmed(command.PhoneNumberConfirmed.Value);

        if (command.TwoFactorEnabled.HasValue)
            user.SetTwoFactorEnabled(command.TwoFactorEnabled.Value);

        if (command.Roles.HasValue)
        {
            // Replace all roles
            user.Roles.Clear();
            var newRoles = command.Roles.Value ?? [];
            foreach (var roleId in newRoles)
            {
                user.AddRole(roleId.Guid);
            }
        }

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return UserErrors.UpdateFailed(result.Errors.Select(e => e.Description));
        }

        return user;
    }
}
