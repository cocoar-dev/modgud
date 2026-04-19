using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Authorization;

public class PermissionRoleProjection : SingleStreamProjection<PermissionRole, Guid>
{
    public PermissionRole Create(PermissionRoleCreatedEvent @event)
    {
        return new PermissionRole
        {
            Id = @event.Id,
            Name = @event.Name,
            Description = @event.Description,
            ResourceType = @event.ResourceType,
            Permissions = @event.Permissions,
            IsDeleted = false
        };
    }

    public PermissionRole Apply(PermissionRoleUpdatedEvent @event, PermissionRole current)
    {
        current.Name = @event.Name;
        current.Description = @event.Description;
        current.ResourceType = @event.ResourceType;
        current.Permissions = @event.Permissions;
        return current;
    }

    public PermissionRole Apply(PermissionRoleDeletedEvent @event, PermissionRole current)
    {
        current.IsDeleted = true;
        return current;
    }
}
