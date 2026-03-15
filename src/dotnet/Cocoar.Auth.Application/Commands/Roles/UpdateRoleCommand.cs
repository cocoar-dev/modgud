using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using Cocoar.Primitives.OptionalAware;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Roles;

/// <summary>
/// Command to update an existing role.
/// </summary>
public record UpdateRoleCommand(
    ShortGuid Id,
    Optional<string> Name,
    Optional<string?> Description,
    Optional<string?> DisplayName,
    Optional<string?> Email,
    Optional<Guid?> BoundToApiResourceId);

/// <summary>
/// Handler for UpdateRoleCommand.
/// </summary>
public class UpdateRoleHandler
{
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IRoleRepository _roleRepository;

    public UpdateRoleHandler(
        RoleManager<ApplicationRole> roleManager,
        IRoleRepository roleRepository)
    {
        _roleManager = roleManager;
        _roleRepository = roleRepository;
    }

    public async Task<ErrorOr<ApplicationRole>> HandleAsync(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetByIdAsync(command.Id.Guid, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound(command.Id.Guid);
        }

        if (command.Name.HasValue)
        {
            var newName = command.Name.Value!;
            var existingRole = await _roleManager.FindByNameAsync(newName);
            if (existingRole is not null && existingRole.Id != role.Id)
            {
                return RoleErrors.DuplicateName(newName);
            }
            role.SetName(newName);
        }

        if (command.Description.HasValue)
        {
            role.SetDescription(command.Description.Value);
        }

        if (command.DisplayName.HasValue)
        {
            role.SetDisplayName(command.DisplayName.Value);
        }

        if (command.Email.HasValue)
        {
            role.SetEmail(command.Email.Value);
        }

        if (command.BoundToApiResourceId.HasValue)
        {
            role.SetBoundToApiResourceId(command.BoundToApiResourceId.Value);
        }

        var result = await _roleManager.UpdateAsync(role);
        if (!result.Succeeded)
        {
            return RoleErrors.UpdateFailed(result.Errors.Select(e => e.Description));
        }

        return role;
    }
}
