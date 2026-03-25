namespace Cocoar.Auth.Api.Hubs;

/// <summary>
/// Sends real-time entity change notifications to connected admin clients.
/// Notifications are automatically scoped to the current realm.
/// </summary>
public interface IAdminHubNotifier
{
	Task EntityChangedAsync(string entityType, string changeType, string? entityId = null);
}
