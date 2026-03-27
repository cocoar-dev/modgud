using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.ReadModels;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence.Repositories;

public class GroupListRepository : IGroupListRepository
{
	private readonly IQuerySession _session;

	public GroupListRepository(IQuerySession session)
	{
		_session = session;
	}

	public async Task<IReadOnlyList<GroupListReadModel>> GetAllAsync(CancellationToken ct = default)
	{
		return await _session.Query<GroupListReadModel>()
			.Where(g => !g.IsArchived)
			.OrderBy(g => g.Name)
			.ToListAsync(ct);
	}
}
