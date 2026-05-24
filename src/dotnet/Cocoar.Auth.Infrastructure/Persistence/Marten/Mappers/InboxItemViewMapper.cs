using System.Text.Json;
using BuildingBlocks.Helper;
using Cocoar.Auth.Application.DTOs.Inbox;
using Cocoar.Auth.Application.Inbox;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Inbox;

namespace Cocoar.Auth.Infrastructure.Persistence.Marten.Mappers;

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
