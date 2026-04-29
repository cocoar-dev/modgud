using Cocoar.Auth.Authorization.Apps;
using Cocoar.Auth.Authorization.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Authorization.Projections;

/// <summary>
/// Inline projection rebuilding an <see cref="App"/> from its event stream.
/// Inline so admin reads + slug-uniqueness checks see new state synchronously
/// after a save.
/// </summary>
public class AppProjection : SingleStreamProjection<App, Guid>
{
    public App Create(AppCreatedEvent @event) => new()
    {
        Id = @event.Id,
        Slug = @event.Slug,
        DisplayName = @event.DisplayName,
        Description = @event.Description,
        Resources = [.. @event.Resources],
        IsSystem = @event.IsSystem,
        IsDeleted = false,
    };

    public App Apply(AppUpdatedEvent @event, App current)
    {
        current.DisplayName = @event.DisplayName;
        current.Description = @event.Description;
        current.Resources = [.. @event.Resources];
        return current;
    }

    public App Apply(AppDeletedEvent @event, App current)
    {
        current.IsDeleted = true;
        return current;
    }
}
