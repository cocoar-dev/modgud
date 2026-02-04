using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Services;

public class UserService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IUserRepository _userRepository;
    private readonly IRoleRepository _roleRepository;

    public UserService(
        UserManager<ApplicationUser> userManager,
        IUserRepository userRepository,
        IRoleRepository roleRepository)
    {
        _userManager = userManager;
        _userRepository = userRepository;
        _roleRepository = roleRepository;
    }

    public async Task<ErrorOr<UserDto>> GetByIdAsync(ShortGuid id, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(id.Guid);
        }

        return UserMapper.ToDto(user);
    }

    public async Task<ErrorOr<UserDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(id, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(id);
        }

        return UserMapper.ToDto(user);
    }

    public async Task<ErrorOr<UserDto>> GetByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByNameAsync(userName);
        if (user is null)
        {
            return UserErrors.NotFoundByUserName(userName);
        }

        return UserMapper.ToDto(user);
    }

    public async Task<ErrorOr<UserListDto>> GetPagedAsync(int page, int pageSize, string? search = null, CancellationToken cancellationToken = default)
    {
        var (users, totalCount) = await _userRepository.GetPagedAsync(page, pageSize, search, cancellationToken);

        return new UserListDto
        {
            Items = users.Select(UserMapper.ToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<ErrorOr<UserDto>> CreateAsync(CreateUserDto dto, CancellationToken cancellationToken = default)
    {
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

    public async Task<ErrorOr<UserDto>> UpdateAsync(ShortGuid id, UpdateUserDto dto, CancellationToken cancellationToken = default)
    {
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

    public async Task<ErrorOr<bool>> DeleteAsync(ShortGuid id, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(id.Guid);
        }

        var result = await _userManager.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return UserErrors.UpdateFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }

    public async Task<ErrorOr<bool>> ResetPasswordAsync(ShortGuid id, ResetPasswordDto dto, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(id.Guid);
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, token, dto.NewPassword);

        if (!result.Succeeded)
        {
            return UserErrors.PasswordChangeFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }

    public async Task<ErrorOr<bool>> ChangePasswordAsync(Guid userId, ChangePasswordDto dto, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(userId);
        }

        var result = await _userManager.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);

        if (!result.Succeeded)
        {
            return UserErrors.PasswordChangeFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }
}
