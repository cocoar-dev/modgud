using Cocoar.Auth.Application.Commands.Roles;
using Cocoar.Auth.Application.Commands.Users;
using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Primitives;

namespace Cocoar.Auth.Api.Extensions;

/// <summary>
/// Extension methods for converting DTOs to Commands.
/// </summary>
public static class DtoExtensions
{
    #region Role Extensions

    /// <summary>
    /// Converts a CreateRoleDto to a CreateRoleCommand.
    /// </summary>
    public static CreateRoleCommand ToCommand(this CreateRoleDto dto) =>
        new(dto.Name, dto.Description);

    /// <summary>
    /// Converts an UpdateRoleDto to an UpdateRoleCommand.
    /// </summary>
    public static UpdateRoleCommand ToCommand(this UpdateRoleDto dto, ShortGuid id) =>
        new(id, dto.Name, dto.Description);

    #endregion

    #region User Extensions

    /// <summary>
    /// Converts a CreateUserDto to a CreateUserCommand.
    /// </summary>
    public static CreateUserCommand ToCommand(this CreateUserDto dto) =>
        new(
            dto.UserName,
            dto.Password,
            dto.Email,
            dto.PhoneNumber,
            dto.FirstName,
            dto.LastName,
            dto.IsActive,
            dto.LockoutEnabled,
            dto.Roles);

    /// <summary>
    /// Converts an UpdateUserDto to an UpdateUserCommand.
    /// </summary>
    public static UpdateUserCommand ToCommand(this UpdateUserDto dto, ShortGuid id) =>
        new(
            id,
            dto.UserName,
            dto.Email,
            dto.PhoneNumber,
            dto.FirstName,
            dto.LastName,
            dto.IsActive,
            dto.LockoutEnabled,
            dto.EmailConfirmed,
            dto.PhoneNumberConfirmed,
            dto.TwoFactorEnabled,
            dto.Roles);

    /// <summary>
    /// Converts a ResetPasswordDto to a ResetUserPasswordCommand.
    /// </summary>
    public static ResetUserPasswordCommand ToCommand(this ResetPasswordDto dto, ShortGuid id) =>
        new(id, dto.NewPassword);

    #endregion
}
