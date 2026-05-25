namespace Modgud.Application.Inbox.Events;

public record InboxItemReadEvent(Guid Id, DateTime ReadAt);
