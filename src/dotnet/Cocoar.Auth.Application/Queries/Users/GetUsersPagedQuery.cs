using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Users;

/// <summary>
/// Query to get a paginated list of users.
/// </summary>
public record GetUsersPagedQuery(int Page, int PageSize, string? Search = null);

/// <summary>
/// Result for GetUsersPagedQuery.
/// </summary>
public record GetUsersPagedResult(IReadOnlyList<UserDetailsReadModel> Users, int TotalCount);

/// <summary>
/// Handler for GetUsersPagedQuery.
/// </summary>
public class GetUsersPagedHandler
{
    private readonly IUserDetailsRepository _userDetailsRepository;

    public GetUsersPagedHandler(IUserDetailsRepository userDetailsRepository)
    {
        _userDetailsRepository = userDetailsRepository;
    }

    public async Task<ErrorOr<GetUsersPagedResult>> HandleAsync(GetUsersPagedQuery query, CancellationToken cancellationToken)
    {
        var (users, totalCount) = await _userDetailsRepository.GetPagedAsync(
            query.Page,
            query.PageSize,
            query.Search,
            cancellationToken);

        return new GetUsersPagedResult(users, totalCount);
    }
}
