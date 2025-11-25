using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Roles;

/// <summary>
/// Command to delete a role.
/// </summary>
public record DeleteRoleCommand(ShortGuid Id);

/// <summary>
/// Handler for DeleteRoleCommand.
/// </summary>
public class DeleteRoleHandler
{
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IRoleRepository _roleRepository;
    private readonly IUserRepository _userRepository;

    public DeleteRoleHandler(
        RoleManager<ApplicationRole> roleManager,
        IRoleRepository roleRepository,
        IUserRepository userRepository)
    {
        _roleManager = roleManager;
        _roleRepository = roleRepository;
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<bool>> HandleAsync(DeleteRoleCommand command, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetByIdAsync(command.Id.Guid, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound(command.Id.Guid);
        }

        // Check if any users have this role
        var usersWithRole = await _userRepository.GetByRoleIdAsync(command.Id.Guid, cancellationToken);
        if (usersWithRole.Count > 0)
        {
            return RoleErrors.CannotDeleteWithUsers;
        }

        var result = await _roleManager.DeleteAsync(role);
        if (!result.Succeeded)
        {
            return RoleErrors.UpdateFailed(result.Errors.Select(e => e.Description));
        }

        return true;
    }
}
