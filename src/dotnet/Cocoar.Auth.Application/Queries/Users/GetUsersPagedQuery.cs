using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Users;

/// <summary>
/// Query to get a paginated list of users.
/// </summary>
public record GetUsersPagedQuery(int Page, int PageSize, string? Search = null);

/// <summary>
/// Result for GetUsersPagedQuery.
/// </summary>
public record GetUsersPagedResult(IReadOnlyList<ApplicationUser> Users, int TotalCount);

/// <summary>
/// Handler for GetUsersPagedQuery.
/// </summary>
public class GetUsersPagedHandler
{
    private readonly IUserRepository _userRepository;

    public GetUsersPagedHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<ErrorOr<GetUsersPagedResult>> HandleAsync(GetUsersPagedQuery query, CancellationToken cancellationToken)
    {
        var (users, totalCount) = await _userRepository.GetPagedAsync(
            query.Page,
            query.PageSize,
            query.Search,
            cancellationToken);

        return new GetUsersPagedResult(users, totalCount);
    }
}
