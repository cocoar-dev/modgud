using Cocoar.Auth.Application.DTOs.Roles;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Roles;

/// <summary>
/// Query to get all roles.
/// </summary>
public record GetAllRolesQuery;

/// <summary>
/// Handler for GetAllRolesQuery.
/// </summary>
public class GetAllRolesHandler
{
    private readonly IRoleRepository _roleRepository;

    public GetAllRolesHandler(IRoleRepository roleRepository)
    {
        _roleRepository = roleRepository;
    }

    public async Task<ErrorOr<RoleListDto>> HandleAsync(GetAllRolesQuery query, CancellationToken cancellationToken)
    {
        var roles = await _roleRepository.GetAllAsync(cancellationToken);

        return new RoleListDto
        {
            Items = roles.Select(RoleMapper.ToDto).ToList(),
            TotalCount = roles.Count
        };
    }
}
