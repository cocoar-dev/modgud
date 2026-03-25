using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.ReadModels;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Users;

/// <summary>
/// Query to get a paginated list of users.
/// </summary>
public record GetUsersPagedQuery(int Page, int PageSize, string? Search = null);

/// <summary>
/// Result for GetUsersPagedQuery.
/// </summary>
public record GetUsersPagedResult(IReadOnlyList<UserListReadModel> Users, int TotalCount);

/// <summary>
/// Handler for GetUsersPagedQuery.
/// Reads from the denormalized UserListReadModel via repository.
/// </summary>
public class GetUsersPagedHandler
{
    private readonly IUserListRepository _repository;

    public GetUsersPagedHandler(IUserListRepository repository)
    {
        _repository = repository;
    }

    public async Task<ErrorOr<GetUsersPagedResult>> HandleAsync(GetUsersPagedQuery query, CancellationToken cancellationToken)
    {
        var (users, totalCount) = await _repository.GetPagedAsync(
            query.Page, query.PageSize, query.Search, cancellationToken);

        return new GetUsersPagedResult(users, totalCount);
    }
}
