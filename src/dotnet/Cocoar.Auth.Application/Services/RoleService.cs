using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Application.Services;

public class RoleService
{
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IRoleRepository _roleRepository;
    private readonly IUserRepository _userRepository;

    public RoleService(
        RoleManager<ApplicationRole> roleManager,
        IRoleRepository roleRepository,
        IUserRepository userRepository)
    {
        _roleManager = roleManager;
        _roleRepository = roleRepository;
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<RoleDto>> GetByIdAsync(ShortGuid id, CancellationToken cancellationToken = default)
    {
        var role = await _roleRepository.GetByIdAsync(id.Guid, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound(id.Guid);
        }

        return RoleMapper.ToDto(role);
    }

    public async Task<ErrorOr<RoleListDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _roleRepository.GetAllAsync(cancellationToken);

        return new RoleListDto
        {
            Items = roles.Select(RoleMapper.ToDto).ToList(),
            TotalCount = roles.Count
        };
    }

    public async Task<ErrorOr<RoleDto>> CreateAsync(CreateRoleDto dto, CancellationToken cancellationToken = default)
    {
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

        return RoleMapper.ToDto(role);
    }

    public async Task<ErrorOr<RoleDto>> UpdateAsync(ShortGuid id, UpdateRoleDto dto, CancellationToken cancellationToken = default)
    {
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

        return RoleMapper.ToDto(role);
    }

    public async Task<ErrorOr<bool>> DeleteAsync(ShortGuid id, CancellationToken cancellationToken = default)
    {
        var role = await _roleRepository.GetByIdAsync(id.Guid, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound(id.Guid);
        }

        // Check if any users have this role
        var usersWithRole = await _userRepository.GetByRoleIdAsync(id.Guid, cancellationToken);
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
