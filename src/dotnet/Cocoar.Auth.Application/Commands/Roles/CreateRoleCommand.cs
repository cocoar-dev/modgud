using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Roles;

/// <summary>
/// Command to create a new role.
/// </summary>
public record CreateRoleCommand(CreateRoleDto Dto);

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

    public async Task<ErrorOr<RoleDto>> HandleAsync(CreateRoleCommand command, CancellationToken cancellationToken)
    {
        var dto = command.Dto;

        var existingRole = await _roleManager.FindByNameAsync(dto.Name);
        if (existingRole is not null)
        {
            return RoleErrors.DuplicateName(dto.Name);
        }

        var role = new ApplicationRole(dto.Name, dto.Description);

        var result = await _roleManager.CreateAsync(role);
        if (!result.Succeeded)
        {
            return RoleErrors.CreationFailed(result.Errors.Select(e => e.Description));
        }

        return new RoleDto
        {
            Id = role.Id,
            Name = role.Name!,
            Description = role.Description,
            CreatedAt = role.CreatedAt,
            ModifiedAt = role.ModifiedAt
        };
    }
}
