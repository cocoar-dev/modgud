using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.ReadModels;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence.Repositories;

public class UserListRepository : IUserListRepository
{
	private readonly IQuerySession _session;

	public UserListRepository(IQuerySession session)
	{
		_session = session;
	}

	public async Task<(IReadOnlyList<UserListReadModel> Users, int TotalCount)> GetPagedAsync(
		int page, int pageSize, string? search = null, CancellationToken ct = default)
	{
		var q = _session.Query<UserListReadModel>()
			.Where(u => !u.IsDeleted);

		if (!string.IsNullOrWhiteSpace(search))
		{
			var s = search.ToLowerInvariant();
			q = q.Where(u =>
				u.UserName.ToLower().Contains(s) ||
				(u.Email != null && u.Email.ToLower().Contains(s)) ||
				(u.FirstName != null && u.FirstName.ToLower().Contains(s)) ||
				(u.LastName != null && u.LastName.ToLower().Contains(s)));
		}

		var totalCount = await q.CountAsync(ct);

		var users = await q
			.OrderBy(u => u.UserName)
			.Skip((page - 1) * pageSize)
			.Take(pageSize)
			.ToListAsync(ct);

		return (users, totalCount);
	}
}
