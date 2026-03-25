using Cocoar.Auth.Application.ReadModels;

namespace Cocoar.Auth.Application.Interfaces;

public interface IUserListRepository
{
	Task<(IReadOnlyList<UserListReadModel> Users, int TotalCount)> GetPagedAsync(
		int page, int pageSize, string? search = null, CancellationToken ct = default);
}
