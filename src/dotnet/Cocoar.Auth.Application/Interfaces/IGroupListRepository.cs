using Cocoar.Auth.Application.ReadModels;

namespace Cocoar.Auth.Application.Interfaces;

public interface IGroupListRepository
{
	Task<IReadOnlyList<GroupListReadModel>> GetAllAsync(CancellationToken ct = default);
}
