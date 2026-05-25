using System.Text.Json;
using BuildingBlocks.Helper;
using Modgud.Application.DTOs.Inbox;
using Modgud.Application.Inbox;
using Modgud.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Modgud.Infrastructure.Persistence.Marten.Mappers;

public static class InboxItemViewMapper
{
    public static InboxItemDto ToDto(this InboxItemView view)
    {
        var descriptor = InboxKindRegistry.Get(view.Kind);

        return new InboxItemDto
        {
            Id = new ShortGuid(view.Id).ToString(),
            Kind = view.Kind.ToString(),
            Severity = view.Severity.ToString(),
            TitleKey = view.TitleKey,
            BodyKey = view.BodyKey,
            Params = view.Params is null
                ? null
                : JsonSerializer.Deserialize<JsonElement>(view.Params.RootElement.GetRawText()),
            Link = view.Link,
            SourceType = view.SourceType,
            SourceId = view.SourceId.HasValue ? new ShortGuid(view.SourceId.Value).ToString() : null,
            CreatedAt = view.CreatedAt,
            ReadAt = view.ReadAt,
            DismissedAt = view.DismissedAt,
            SnoozeUntil = view.SnoozeUntil,
            Persistence = descriptor.Persistence.ToString(),
            Actionable = descriptor.Actionable,
            Icon = descriptor.Icon,
        };
    }
}
