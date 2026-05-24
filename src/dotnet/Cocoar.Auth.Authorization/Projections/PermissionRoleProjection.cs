using Cocoar.Auth.Authorization.Events;
using Cocoar.Auth.Authorization.Roles;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Authorization.Projections;

/// <summary>
/// Inline projection rebuilding a <see cref="PermissionRole"/> from its event stream.
/// Inline so the admin UI sees the new role state immediately after a save.
/// </summary>
public partial class PermissionRoleProjection : SingleStreamProjection<PermissionRole, Guid>
{
    public PermissionRole Create(PermissionRoleCreatedEvent @event) => new()
    {
        Id = @event.Id,
        Name = @event.Name,
        Description = @event.Description,
        AppId = @event.AppId,
        IsRealmAdmin = @event.IsRealmAdmin,
        PermissionIds = [.. @event.PermissionIds],
        IsDeleted = false,
    };

    public PermissionRole Apply(PermissionRoleUpdatedEvent @event, PermissionRole current)
    {
        current.Name = @event.Name;
        current.Description = @event.Description;
        current.AppId = @event.AppId;
        current.IsRealmAdmin = @event.IsRealmAdmin;
        current.PermissionIds = [.. @event.PermissionIds];
        return current;
    }

    public PermissionRole Apply(PermissionRoleDeletedEvent @event, PermissionRole current)
    {
        current.IsDeleted = true;
        return current;
    }
}
