namespace Cocoar.Auth.Application.Notifications;

/// <summary>
/// Fire-and-forget notification published when an admin entity changes.
/// Can be published from projection side effects (MultiStreamProjection.RaiseSideEffects)
/// or manually from controllers via IAdminHubNotifier.
/// </summary>
public record EntityChangedNotification(string EntityType, string ChangeType, string? EntityId = null);
