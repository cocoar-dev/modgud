using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.ReadModels;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Roles;

/// <summary>
/// Query to get all roles.
/// </summary>
public record GetAllRolesQuery;

/// <summary>
/// Handler for GetAllRolesQuery.
/// Reads from the denormalized RoleListReadModel via repository.
/// </summary>
public class GetAllRolesHandler
{
    private readonly IRoleListRepository _repository;

    public GetAllRolesHandler(IRoleListRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<IReadOnlyList<RoleListReadModel>>> HandleAsync(GetAllRolesQuery query, CancellationToken cancellationToken)
    {
        var roles = await _repository.GetAllAsync(cancellationToken);
        return roles.ToList();
    }
}
