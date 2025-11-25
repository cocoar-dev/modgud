using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Primitives;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Users;

/// <summary>
/// Query to get a user by ID.
/// </summary>
public record GetUserByIdQuery(ShortGuid Id);

/// <summary>
/// Handler for GetUserByIdQuery.
/// </summary>
public class GetUserByIdHandler
{
    private readonly IUserRepository _userRepository;

    public GetUserByIdHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<ApplicationUser>> HandleAsync(GetUserByIdQuery query, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(query.Id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(query.Id.Guid);
        }

        return user;
    }
}
