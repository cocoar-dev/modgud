namespace Cocoar.Auth.Application.Inbox.Events;

public record InboxItemDismissedEvent(Guid Id, DateTime DismissedAt);
