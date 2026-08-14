using Marten.Events.Aggregation;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Projections;

/// <summary>
/// Builds Group documents inline from group streams. Group is mapped as a
/// concrete subclass of Principal, so Marten stores the result in the shared
/// principal table while this projection remains independent from Person events.
/// </summary>
public partial class GroupProjection : SingleStreamProjection<Group, Guid>
{
    public Group Create(GroupCreatedEvent @event) => new()
    {
        Id = @event.Id,
        Name = @event.Name,
        Description = @event.Description,
        MemberIds = @event.MemberIds,
        RoleIds = @event.RoleIds,
        MembershipMode = @event.MembershipMode,
        MembershipScript = @event.MembershipScript,
        CompiledMembershipScript = @event.CompiledMembershipScript,
        MembershipScriptDependencies = @event.MembershipScriptDependencies,
        Email = @event.Email,
        EmailMode = @event.EmailMode,
        BoundTo = @event.BoundTo ?? [],
        ExternallyDrivable = @event.ExternallyDrivable,
        IsActive = true,
        IsDeleted = false,
    };

    public Group Apply(GroupUpdatedEvent @event, Group group)
    {
        group.Name = @event.Name;
        group.Description = @event.Description;
        group.MemberIds = @event.MemberIds;
        group.RoleIds = @event.RoleIds;
        group.MembershipMode = @event.MembershipMode;
        group.MembershipScript = @event.MembershipScript;
        group.CompiledMembershipScript = @event.CompiledMembershipScript;
        group.MembershipScriptDependencies = @event.MembershipScriptDependencies;
        group.Email = @event.Email;
        group.EmailMode = @event.EmailMode;
        group.BoundTo = @event.BoundTo ?? [];
        group.ExternallyDrivable = @event.ExternallyDrivable;
        group.MembershipLastError = null;
        return group;
    }

    public Group Apply(GroupMembershipRecomputedEvent @event, Group group)
    {
        group.MemberIds = @event.MemberIds;
        group.MembershipLastError = null;
        return group;
    }

    public Group Apply(GroupMembershipRecomputeFailedEvent @event, Group group)
    {
        group.MembershipLastError = @event.Error;
        return group;
    }

    public Group Apply(GroupDeletedEvent @event, Group group)
    {
        group.IsDeleted = true;
        group.IsActive = false;
        return group;
    }
}
