namespace Modgud.Application.Inbox.Events;

public record InboxItemDismissedEvent(Guid Id, DateTime DismissedAt);
