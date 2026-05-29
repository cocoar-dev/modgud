using Marten.Events.Aggregation;
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;

namespace Modgud.Authorization.Projections;

/// <summary>
/// Inline projection that builds the polymorphic Principal table from group
/// events (lib-owned). The consuming app subclasses this and adds Create/Apply
/// methods for its own Person-side events (UserCreated, UserUpdated, etc.) —
/// both Person and Group documents land in <c>mt_doc_principal</c>, distinguished
/// by Marten's sub-class discriminator.
/// </summary>
public abstract partial class PrincipalProjectionBase : SingleStreamProjection<Principal, Guid>
{
    public Principal Create(GroupCreatedEvent @event) => new Group
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

    public Principal Apply(GroupUpdatedEvent @event, Principal current)
    {
        if (current is not Group group) return current;
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

    public Principal Apply(GroupMembershipRecomputedEvent @event, Principal current)
    {
        if (current is not Group group) return current;
        group.MemberIds = @event.MemberIds;
        group.MembershipLastError = null;
        return group;
    }

    public Principal Apply(GroupMembershipRecomputeFailedEvent @event, Principal current)
    {
        if (current is not Group group) return current;
        group.MembershipLastError = @event.Error;
        return group;
    }

    public Principal Apply(GroupDeletedEvent @event, Principal current)
    {
        current.IsDeleted = true;
        current.IsActive = false;
        return current;
    }
}
