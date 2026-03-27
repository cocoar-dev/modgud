using Cocoar.Auth.Application.Models;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository for group aggregate operations.
/// Abstracts event store access so the Application layer doesn't depend on Marten.
/// </summary>
public interface IGroupRepository
{
	Task<GroupState?> LoadStateAsync(Guid id, CancellationToken ct = default);
	Task<IReadOnlyList<GroupState>> QueryAllActiveStatesAsync(CancellationToken ct = default);
	Task StartStreamAsync(Guid id, object @event, CancellationToken ct = default);
	Task AppendEventsAsync(Guid id, object[] events, CancellationToken ct = default);

	/// <summary>
	/// Reloads GroupState from DB, bypassing the session identity map cache.
	/// Use after appending events when you need the updated projection state.
	/// </summary>
	Task<GroupState?> ReloadStateAsync(Guid id, CancellationToken ct = default);
}
