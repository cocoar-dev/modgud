using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Users;

/// <summary>
/// Command to update an existing user.
/// </summary>
public record UpdateUserCommand(ShortGuid Id, UpdateUserDto Dto);

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

    public async Task<ErrorOr<UserDto>> HandleAsync(UpdateUserCommand command, CancellationToken cancellationToken)
    {
        var (id, dto) = command;

        var user = await _userRepository.GetByIdAsync(id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(id.Guid);
        }

        if (dto.UserName.HasValue)
        {
            var newUserName = dto.UserName.Value!;
            var existingUser = await _userManager.FindByNameAsync(newUserName);
            if (existingUser is not null && existingUser.Id != user.Id)
            {
                return UserErrors.DuplicateUserName(newUserName);
            }
            user.SetUserName(newUserName);
        }

        if (dto.Email.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(dto.Email.Value))
            {
                var existingUser = await _userManager.FindByEmailAsync(dto.Email.Value);
                if (existingUser is not null && existingUser.Id != user.Id)
                {
                    return UserErrors.DuplicateEmail(dto.Email.Value);
                }
            }
            user.SetEmail(dto.Email.Value);
        }

        if (dto.PhoneNumber.HasValue)
            user.SetPhoneNumber(dto.PhoneNumber.Value);

        if (dto.FirstName.HasValue)
            user.SetFirstName(dto.FirstName.Value);

        if (dto.LastName.HasValue)
            user.SetLastName(dto.LastName.Value);

        if (dto.IsActive.HasValue)
            user.SetIsActive(dto.IsActive.Value);

        if (dto.LockoutEnabled.HasValue)
            user.SetLockoutEnabled(dto.LockoutEnabled.Value);

        if (dto.EmailConfirmed.HasValue)
            user.SetEmailConfirmed(dto.EmailConfirmed.Value);

        if (dto.PhoneNumberConfirmed.HasValue)
            user.SetPhoneNumberConfirmed(dto.PhoneNumberConfirmed.Value);

        if (dto.TwoFactorEnabled.HasValue)
            user.SetTwoFactorEnabled(dto.TwoFactorEnabled.Value);

        if (dto.Roles.HasValue)
        {
            // Replace all roles
            user.Roles.Clear();
            var newRoles = dto.Roles.Value ?? [];
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

        return UserMapper.ToDto(user);
    }
}
