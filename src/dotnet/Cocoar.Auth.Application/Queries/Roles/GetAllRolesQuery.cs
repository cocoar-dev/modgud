using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
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

    public async Task<ErrorOr<IReadOnlyList<ApplicationRole>>> HandleAsync(GetAllRolesQuery query, CancellationToken cancellationToken)
    {
        var roles = await _roleRepository.GetAllAsync(cancellationToken);
        return roles.ToList();
    }
}
