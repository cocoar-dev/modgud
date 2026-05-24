namespace Cocoar.Auth.Application.Inbox.Events;

public record InboxItemSnoozedEvent(Guid Id, DateTime? SnoozeUntil);
