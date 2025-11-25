using Cocoar.Auth.Application.Errors;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
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
    private readonly IUserDetailsRepository _userDetailsRepository;

    public GetUserByIdHandler(IUserDetailsRepository userDetailsRepository)
    {
        _userDetailsRepository = userDetailsRepository;
    }

    public async Task<ErrorOr<UserDetailsReadModel>> HandleAsync(GetUserByIdQuery query, CancellationToken cancellationToken)
    {
        var user = await _userDetailsRepository.GetByIdAsync(query.Id.Guid, cancellationToken);
        if (user is null)
        {
            return UserErrors.NotFound(query.Id.Guid);
        }

        return user;
    }
}
