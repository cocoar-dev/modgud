using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Roles;

/// <summary>
/// Query to get a role by ID.
/// </summary>
public record GetRoleByIdQuery(ShortGuid Id);

/// <summary>
/// Handler for GetRoleByIdQuery.
/// </summary>
public class GetRoleByIdHandler
{
    private readonly IRoleRepository _roleRepository;

    public GetRoleByIdHandler(IRoleRepository roleRepository)
    {
        _roleRepository = roleRepository;
    }

    public async Task<ErrorOr<ApplicationRole>> HandleAsync(GetRoleByIdQuery query, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetByIdAsync(query.Id.Guid, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound(query.Id.Guid);
        }

        return role;
    }
}
