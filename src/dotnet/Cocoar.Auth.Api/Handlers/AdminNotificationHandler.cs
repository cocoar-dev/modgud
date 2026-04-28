using Cocoar.Auth.Api.Hubs;
using Cocoar.Auth.Application.Notifications;
using Cocoar.SignalARRR.Server;
using Cocoar.SignalARRR.Server.ExtensionMethods;

namespace Cocoar.Auth.Api.Handlers;

/// <summary>
/// Wolverine handler for fire-and-forget admin notifications via SignalARRR.
/// Can be triggered by projection side effects (RaiseSideEffects → PublishMessage)
/// or directly via IMessageBus.PublishAsync from controllers.
/// </summary>
public static class AdminNotificationHandler
{
	public static async Task Handle(
		EntityChangedNotification notification,
		ClientManager clients)
	{
		// Broadcast to all admins in all realms
		// TODO: scope to specific realm when TenantId is available on MessageContext
		await clients.WithHub<AdminHub>()
			.SendAsync<IAdminHubClient>(c =>
				c.OnEntityChanged(notification.EntityType, notification.ChangeType, notification.EntityId));
	}
}
