using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Commands.Roles;

/// <summary>
/// Command to update an existing role.
/// </summary>
public record UpdateRoleCommand(ShortGuid Id, UpdateRoleDto Dto);

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

    public async Task<ErrorOr<RoleDto>> HandleAsync(UpdateRoleCommand command, CancellationToken cancellationToken)
    {
        var (id, dto) = command;

        var role = await _roleRepository.GetByIdAsync(id.Guid, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound(id.Guid);
        }

        if (dto.Name.HasValue)
        {
            var newName = dto.Name.Value!;
            var existingRole = await _roleManager.FindByNameAsync(newName);
            if (existingRole is not null && existingRole.Id != role.Id)
            {
                return RoleErrors.DuplicateName(newName);
            }
            role.SetName(newName);
        }

        if (dto.Description.HasValue)
        {
            role.SetDescription(dto.Description.Value);
        }

        var result = await _roleManager.UpdateAsync(role);
        if (!result.Succeeded)
        {
            return RoleErrors.UpdateFailed(result.Errors.Select(e => e.Description));
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
