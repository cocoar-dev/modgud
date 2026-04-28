using Cocoar.SignalARRR.Contracts;

namespace Cocoar.Auth.Api.Hubs;

/// <summary>
/// Server → Client contract for admin entity change notifications.
/// Clients receive entity type, change type, and optional entity ID.
/// </summary>
[SignalARRRContract]
public interface IAdminHubClient
{
	/// <summary>An entity was created, updated, or deleted.</summary>
	Task OnEntityChanged(string entityType, string changeType, string? entityId);
}
