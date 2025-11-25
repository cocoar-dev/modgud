using Cocoar.Auth.Application.DTOs.Users;
using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Mappers;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Users;

/// <summary>
/// Query to get a paginated list of users.
/// </summary>
public record GetUsersPagedQuery(int Page, int PageSize, string? Search = null);

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

    public async Task<ErrorOr<UserListDto>> HandleAsync(GetUsersPagedQuery query, CancellationToken cancellationToken)
    {
        var (users, totalCount) = await _userRepository.GetPagedAsync(
            query.Page,
            query.PageSize,
            query.Search,
            cancellationToken);

        return new UserListDto
        {
            Items = users.Select(UserMapper.ToDto).ToList(),
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }
}
