using System.Text.Json;

namespace Cocoar.Auth.Application.Inbox.Events;

public record InboxItemCreatedEvent(
    Guid Id,
    Guid RecipientUserId,
    InboxKind Kind,
    InboxSeverity Severity,
    string TitleKey,
    string? BodyKey,
    JsonDocument? Params,
    string? Link,
    string? SourceType,
    Guid? SourceId,
    DateTime CreatedAt);
