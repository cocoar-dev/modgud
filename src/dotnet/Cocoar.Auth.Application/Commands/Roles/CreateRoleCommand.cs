using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Roles;

/// <summary>
/// Command to create a new role.
/// ClientId null = realm role, set = client role.
/// </summary>
public record CreateRoleCommand(
    string Name,
    string? Description,
    string? DisplayName = null,
    string? Email = null,
    Guid? ClientId = null,
    List<string>? Scopes = null);

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

        var role = new ApplicationRole(command.Name, command.Description, command.ClientId);
        role.SetDisplayName(command.DisplayName);
        role.SetEmail(command.Email);
        if (command.Scopes is { Count: > 0 })
            role.SetScopes(command.Scopes);

        var result = await _roleManager.CreateAsync(role);
        if (!result.Succeeded)
        {
            return RoleErrors.CreationFailed(result.Errors.Select(e => e.Description));
        }

        return role;
    }
}
