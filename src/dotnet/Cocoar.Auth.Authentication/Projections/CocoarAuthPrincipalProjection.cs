using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Authorization.Projections;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Domain.Users.Events;

namespace Cocoar.Auth.Authentication.Projections;

/// <summary>
/// Inline projection feeding the unified <see cref="Principal"/> document table
/// from user-stream events. The library base owns all group-stream events; this
/// class adds the Person side (Person).
/// </summary>
public class CocoarAuthPrincipalProjection : PrincipalProjectionBase
{
    // SingleStreamProjection uses the event's stream-id automatically — user events
    // live on user streams (stream-id = user-id), group events on group streams
    // (stream-id = group-id). Both land in the same polymorphic Principal document
    // table keyed by stream-id, distinguished by the Marten sub-class discriminator.

    public Principal Create(UserCreatedEvent @event) => CreatePerson(
        @event.Id,
        @event.Firstname.OrDefault(),
        @event.Lastname.OrDefault(),
        @event.Acronym.OrDefault(),
        @event.Email.OrDefault());

    public Principal Create(UserMigratedEvent @event) => CreatePerson(
        @event.Id,
        @event.Firstname.OrDefault(),
        @event.Lastname.OrDefault(),
        @event.Acronym.OrDefault(),
        @event.Email.OrDefault());

    public Principal Apply(UserUpdatedEvent @event, Principal current)
    {
        if (current is not Person person) return current;
        if (@event.Firstname.HasValue) person.Firstname = @event.Firstname.Value;
        if (@event.Lastname.HasValue) person.Lastname = @event.Lastname.Value;
        if (@event.Acronym.HasValue) person.Acronym = @event.Acronym.Value;
        if (@event.Email.HasValue)
        {
            person.Email = @event.Email.Value;
            person.NormalizedEmail = person.Email?.ToUpperInvariant();
        }
        return person;
    }

    public Principal Apply(UserIdentitySetupEvent @event, Principal current)
    {
        if (current is not Person person) return current;
        person.AccountName = @event.UserName;
        person.NormalizedUserName = @event.UserName.ToUpperInvariant();
        person.IsActive = @event.IsActive;
        return person;
    }

    public Principal Apply(UserUserNameChangedEvent @event, Principal current)
    {
        if (current is not Person person) return current;
        person.AccountName = @event.UserName;
        person.NormalizedUserName = @event.UserName.ToUpperInvariant();
        return person;
    }

    public Principal Apply(UserActivatedEvent @event, Principal current)
    {
        current.IsActive = true;
        return current;
    }

    public Principal Apply(UserDeactivatedEvent @event, Principal current)
    {
        current.IsActive = false;
        return current;
    }

    public Principal Apply(UserDeletedEvent @event, Principal current)
    {
        current.IsDeleted = true;
        current.IsActive = false;
        return current;
    }

    public Principal Apply(UserExternalIdentityLinkedEvent @event, Principal current)
    {
        if (current is not Person person) return current;
        var newRef = new ExternalIdentityRef(@event.LinkId, @event.LoginProviderId, @event.Issuer);
        person.ExternalIdentities = person.ExternalIdentities
            .Where(r => r.LinkId != @event.LinkId)
            .Append(newRef)
            .ToList();
        return person;
    }

    public Principal Apply(UserExternalIdentityUnlinkedEvent @event, Principal current)
    {
        if (current is not Person person) return current;
        person.ExternalIdentities = person.ExternalIdentities
            .Where(r => r.LinkId != @event.LinkId)
            .ToList();
        return person;
    }

    private static Person CreatePerson(
        Guid id, string? firstname, string? lastname, string? acronym, string? email)
        => new()
        {
            Id = id,
            Firstname = firstname,
            Lastname = lastname,
            Acronym = acronym,
            Email = email,
            NormalizedEmail = email?.ToUpperInvariant(),
            IsActive = true,
            IsDeleted = false,
        };
}
