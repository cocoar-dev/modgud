namespace Modgud.Application.Inbox.Events;

public record InboxItemSnoozedEvent(Guid Id, DateTime? SnoozeUntil);
