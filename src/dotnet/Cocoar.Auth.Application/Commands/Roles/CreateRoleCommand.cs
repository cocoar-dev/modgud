using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Roles;

/// <summary>
/// Command to create a new role.
/// </summary>
public record CreateRoleCommand(
    string Name,
    string? Description,
    string? DisplayName = null,
    string? Email = null,
    Guid? BoundToApiResourceId = null);

/// <summary>
/// Handler for CreateRoleCommand.
/// </summary>
public class CreateRoleHandler
{
    private readonly RoleManager<ApplicationRole> _roleManager;

    public CreateRoleHandler(RoleManager<ApplicationRole> roleManager)
    {
        _roleManager = roleManager;
    }

    public async Task<ErrorOr<ApplicationRole>> HandleAsync(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        var existingRole = await _roleManager.FindByNameAsync(command.Name);
        if (existingRole is not null)
        {
            return RoleErrors.DuplicateName(command.Name);
        }

        var role = new ApplicationRole(command.Name, command.Description);
        role.SetDisplayName(command.DisplayName);
        role.SetEmail(command.Email);
        role.SetBoundToApiResourceId(command.BoundToApiResourceId);

        var result = await _roleManager.CreateAsync(role);
        if (!result.Succeeded)
        {
            return RoleErrors.CreationFailed(result.Errors.Select(e => e.Description));
        }

        return role;
    }
}
