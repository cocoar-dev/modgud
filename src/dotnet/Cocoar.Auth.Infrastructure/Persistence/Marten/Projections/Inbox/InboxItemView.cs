using System.Text.Json;
using Marten.Schema;
using Cocoar.Auth.Application.Inbox;

namespace Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Inbox;

[DocumentAlias("inbox_item_view")]
public record InboxItemView
{
    public Guid Id { get; init; }
    public Guid RecipientUserId { get; init; }
    public InboxKind Kind { get; init; }
    public InboxSeverity Severity { get; init; }
    public string TitleKey { get; init; } = string.Empty;
    public string? BodyKey { get; init; }
    public JsonDocument? Params { get; init; }
    public string? Link { get; init; }
    public string? SourceType { get; init; }
    public Guid? SourceId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ReadAt { get; init; }
    public DateTime? DismissedAt { get; init; }
    public DateTime? SnoozeUntil { get; init; }
}
