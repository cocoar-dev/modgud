using Cocoar.Auth.Domain.Authorization;
using Cocoar.Auth.Domain.Authorization.Events;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Authorization;

public class AuthorizationGroupProjection : SingleStreamProjection<AuthorizationGroup, Guid>
{
    public AuthorizationGroup Create(AuthorizationGroupCreatedEvent @event)
    {
        return new AuthorizationGroup
        {
            Id = @event.Id,
            Name = @event.Name,
            Description = @event.Description,
            MemberIds = @event.MemberIds,
            RoleIds = @event.RoleIds,
            AccessScripts = @event.AccessScripts,
            MembershipMode = @event.MembershipMode,
            MembershipScript = @event.MembershipScript,
            CompiledMembershipScript = @event.CompiledMembershipScript,
            MembershipScriptDependencies = @event.MembershipScriptDependencies,
            Email = @event.Email,
            EmailMode = @event.EmailMode,
            IsDeleted = false
        };
    }

    public AuthorizationGroup Apply(AuthorizationGroupUpdatedEvent @event, AuthorizationGroup current)
    {
        current.Name = @event.Name;
        current.Description = @event.Description;
        current.MemberIds = @event.MemberIds;
        current.RoleIds = @event.RoleIds;
        current.AccessScripts = @event.AccessScripts;
        current.MembershipMode = @event.MembershipMode;
        current.MembershipScript = @event.MembershipScript;
        current.CompiledMembershipScript = @event.CompiledMembershipScript;
        current.MembershipScriptDependencies = @event.MembershipScriptDependencies;
        current.Email = @event.Email;
        current.EmailMode = @event.EmailMode;
        // Clear stale error — the recalculator reports fresh status via
        // MembershipRecomputed / MembershipRecomputeFailed.
        current.MembershipLastError = null;
        return current;
    }

    public AuthorizationGroup Apply(AuthorizationGroupMembershipRecomputedEvent @event, AuthorizationGroup current)
    {
        current.MemberIds = @event.MemberIds;
        current.MembershipLastError = null;
        return current;
    }

    public AuthorizationGroup Apply(AuthorizationGroupMembershipRecomputeFailedEvent @event, AuthorizationGroup current)
    {
        current.MembershipLastError = @event.Error;
        return current;
    }

    public AuthorizationGroup Apply(AuthorizationGroupDeletedEvent @event, AuthorizationGroup current)
    {
        current.IsDeleted = true;
        return current;
    }
}
