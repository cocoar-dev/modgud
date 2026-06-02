using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Domain.Users.Events;
using Modgud.Infrastructure.Events;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Authentication.Projections;

public partial class UserViewProjection : MultiStreamProjection<UserView, Guid>
{
    public UserViewProjection()
    {
        // User-eigene Events (Stream-ID = User-ID)
        Identity<UserCreatedEvent>(e => e.Id);
        Identity<UserMigratedEvent>(e => e.Id);
        Identity<UserUpdatedEvent>(e => e.Id);
        Identity<UserIdentitySetupEvent>(e => e.UserId);
        Identity<UserUserNameChangedEvent>(e => e.UserId);
        Identity<UserActivatedEvent>(e => e.UserId);
        Identity<UserDeactivatedEvent>(e => e.UserId);
        Identity<UserDeletedEvent>(e => e.Id);
        Identity<UserPasswordChangedEvent>(e => e.UserId);

        // External-identity link events live on the user's stream (the mirror
        // copies emitted by ExternalLoginProcessor / DeleteUsersHandler). Use
        // them to track which IdPs the user is linked with.
        Identity<UserExternalIdentityLinkedEvent>(e => e.UserId);
        Identity<UserExternalIdentityUnlinkedEvent>(e => e.UserId);
    }

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<UserView> slice)
    {
        var snapshot = slice.Snapshot;
        if (snapshot is null || !ProjectionSideEffects.Enabled)
            return ValueTask.CompletedTask;

        var action = slice.Events().Any(e => e.Data is UserDeletedEvent)
            ? SignalRDispatchAction.Deleted
            : slice.Events().Any(e => e.Data is UserCreatedEvent or UserMigratedEvent)
                ? SignalRDispatchAction.Created
                : SignalRDispatchAction.Updated;

        slice.PublishMessage(new UserViewSignalRDispatch(action, snapshot, snapshot.Id));

        return ValueTask.CompletedTask;
    }

    // ── User-eigene Events ──────────────────────────────────────────

    public UserView Create(UserCreatedEvent @event)
    {
        return new UserView
        {
            Id = @event.Id,
            Firstname = @event.Firstname.OrDefault(),
            Lastname = @event.Lastname.OrDefault(),
            Acronym = @event.Acronym.OrDefault(),
            Email = @event.Email.OrDefault(),
            IsDeleted = false
        };
    }

    public UserView Create(UserMigratedEvent @event)
    {
        return new UserView
        {
            Id = @event.Id,
            Firstname = @event.Firstname.OrDefault(),
            Lastname = @event.Lastname.OrDefault(),
            Acronym = @event.Acronym.OrDefault(),
            Email = @event.Email.OrDefault(),
            IsDeleted = false
        };
    }

    public UserView Apply(UserUpdatedEvent @event, UserView current)
    {
        return current with
        {
            Firstname = @event.Firstname.HasValue ? @event.Firstname.Value : current.Firstname,
            Lastname = @event.Lastname.HasValue ? @event.Lastname.Value : current.Lastname,
            Acronym = @event.Acronym.HasValue ? @event.Acronym.Value : current.Acronym,
            Email = @event.Email.HasValue ? @event.Email.Value : current.Email
        };
    }

    public UserView Apply(UserDeletedEvent @event, UserView current)
    {
        return current with { IsDeleted = true };
    }

    public UserView Apply(UserIdentitySetupEvent @event, UserView current)
    {
        return current with
        {
            UserName = @event.UserName,
            IsActive = @event.IsActive
        };
    }

    public UserView Apply(UserUserNameChangedEvent @event, UserView current)
    {
        return current with { UserName = @event.UserName };
    }

    public UserView Apply(UserActivatedEvent @event, UserView current)
    {
        return current with { IsActive = true };
    }

    public UserView Apply(UserDeactivatedEvent @event, UserView current)
    {
        return current with { IsActive = false };
    }

    public UserView Apply(UserPasswordChangedEvent @event, UserView current)
    {
        return current with { HasPassword = true };
    }

    public UserView Apply(UserExternalIdentityLinkedEvent @event, UserView current)
    {
        if (current.ExternalLoginProviderIds.Contains(@event.LoginProviderId)) return current;
        return current with
        {
            ExternalLoginProviderIds = [.. current.ExternalLoginProviderIds, @event.LoginProviderId],
        };
    }

    public UserView Apply(UserExternalIdentityUnlinkedEvent @event, UserView current)
    {
        // Deduplicated provider-id set (not per-link): unlinking one of two links
        // from the SAME provider drops the provider from this indicator. Accepted
        // cosmetic limitation — see UserView.ExternalLoginProviderIds docs. Authz
        // never reads this; Person.ExternalIdentities is the per-link source.
        if (!current.ExternalLoginProviderIds.Contains(@event.LoginProviderId)) return current;
        return current with
        {
            ExternalLoginProviderIds = current.ExternalLoginProviderIds
                .Where(id => id != @event.LoginProviderId)
                .ToList(),
        };
    }
}
