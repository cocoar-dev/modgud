using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.ReadModels;
using ErrorOr;

namespace Cocoar.Auth.Application.Queries.Groups;

public record GetAllGroupsQuery;

public class GetAllGroupsHandler
{
	private readonly IGroupListRepository _repository;

	public GetAllGroupsHandler(IGroupListRepository repository)
	{
		_repository = repository;
	}

	public async Task<ErrorOr<IReadOnlyList<GroupListReadModel>>> HandleAsync(GetAllGroupsQuery query, CancellationToken cancellationToken)
	{
		var groups = await _repository.GetAllAsync(cancellationToken);
		return groups.ToList();
	}
}
