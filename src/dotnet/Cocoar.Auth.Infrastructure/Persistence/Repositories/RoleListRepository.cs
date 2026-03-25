using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.ReadModels;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence.Repositories;

public class RoleListRepository : IRoleListRepository
{
	private readonly IQuerySession _session;

	public RoleListRepository(IQuerySession session)
	{
		_session = session;
	}

	public async Task<IReadOnlyList<RoleListReadModel>> GetAllAsync(CancellationToken ct = default)
	{
		return await _session.Query<RoleListReadModel>()
			.Where(r => !r.IsDeleted)
			.OrderBy(r => r.Name)
			.ToListAsync(ct);
	}
}
