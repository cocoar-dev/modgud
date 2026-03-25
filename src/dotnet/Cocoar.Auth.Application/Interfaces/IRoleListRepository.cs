using Cocoar.Auth.Application.ReadModels;

namespace Cocoar.Auth.Application.Interfaces;

public interface IRoleListRepository
{
	Task<IReadOnlyList<RoleListReadModel>> GetAllAsync(CancellationToken ct = default);
}
