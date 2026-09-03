using Modgud.Authorization.Apps;
using Modgud.Authorization.Events;
using Marten.Events.Aggregation;

namespace Modgud.Authorization.Projections;

/// <summary>
/// Inline projection rebuilding an <see cref="App"/> from its event stream.
/// Inline so admin reads + slug-uniqueness checks see new state synchronously
/// after a save.
/// </summary>
public partial class AppProjection : SingleStreamProjection<App, Guid>
{
    // Apply (not Create) so a Created event on an EXISTING stream REVIVES the entity:
    // provisioning re-imports a soft-deleted entity under its pinned id, and the fresh
    // document replaces the old one wholesale (IsDeleted back to false, no stale field).
    public App Apply(AppCreatedEvent @event, App _) => new()
    {
        Id = @event.Id,
        Slug = @event.Slug,
        DisplayName = @event.DisplayName,
        Description = @event.Description,
        Permissions = [.. @event.Permissions],
        IsSystem = @event.IsSystem,
        IsDeleted = false,
    };

    public App Apply(AppUpdatedEvent @event, App current)
    {
        current.DisplayName = @event.DisplayName;
        current.Description = @event.Description;
        current.Permissions = [.. @event.Permissions];
        return current;
    }

    public App Apply(AppDeletedEvent @event, App current)
    {
        current.IsDeleted = true;
        return current;
    }
}
