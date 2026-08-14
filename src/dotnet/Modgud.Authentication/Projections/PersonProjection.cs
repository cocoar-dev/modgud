using Marten.Events.Aggregation;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.Users.Events;

namespace Modgud.Authentication.Projections;

/// <summary>
/// Builds Person documents inline from user streams. Person is mapped as a
/// concrete subclass of Principal, so it shares the principal table with Group
/// without relying on projection-class inheritance across assemblies.
/// </summary>
public partial class PersonProjection : SingleStreamProjection<Person, Guid>
{
    public PersonProjection()
    {
        // Person, Group, and ServiceAccount are subclasses in the same physical
        // mt_doc_principal table. Marten's default projection teardown truncates
        // the root table, so rebuilding PersonProjection by itself would also
        // delete every Group and directly stored ServiceAccount. The supported
        // PrincipalProjectionRebuilder replays both event-sourced principal
        // projections in place, then performs subtype-scoped stale-row cleanup.
        Options.TeardownDataOnRebuild = false;

        // Defining this constructor suppresses the source generator's generated
        // IncludeType constructor, so keep the event allow-list explicit here.
        IncludeType<UserCreatedEvent>();
        IncludeType<UserMigratedEvent>();
        IncludeType<UserUpdatedEvent>();
        IncludeType<UserIdentitySetupEvent>();
        IncludeType<UserUserNameChangedEvent>();
        IncludeType<UserActivatedEvent>();
        IncludeType<UserDeactivatedEvent>();
        IncludeType<UserDeletedEvent>();
        IncludeType<UserExternalIdentityLinkedEvent>();
        IncludeType<UserExternalIdentityUnlinkedEvent>();
    }

    public Person Create(UserCreatedEvent @event) => CreatePerson(
        @event.Id,
        @event.Firstname.OrDefault(),
        @event.Lastname.OrDefault(),
        @event.Acronym.OrDefault(),
        @event.Email.OrDefault());

    public Person Create(UserMigratedEvent @event) => CreatePerson(
        @event.Id,
        @event.Firstname.OrDefault(),
        @event.Lastname.OrDefault(),
        @event.Acronym.OrDefault(),
        @event.Email.OrDefault());

    public Person Apply(UserUpdatedEvent @event, Person person)
    {
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

    public Person Apply(UserIdentitySetupEvent @event, Person person)
    {
        person.AccountName = @event.UserName;
        person.NormalizedUserName = @event.UserName.ToUpperInvariant();
        person.IsActive = @event.IsActive;
        return person;
    }

    public Person Apply(UserUserNameChangedEvent @event, Person person)
    {
        person.AccountName = @event.UserName;
        person.NormalizedUserName = @event.UserName.ToUpperInvariant();
        return person;
    }

    public Person Apply(UserActivatedEvent @event, Person person)
    {
        person.IsActive = true;
        return person;
    }

    public Person Apply(UserDeactivatedEvent @event, Person person)
    {
        person.IsActive = false;
        return person;
    }

    public Person Apply(UserDeletedEvent @event, Person person)
    {
        person.IsDeleted = true;
        person.IsActive = false;
        return person;
    }

    public Person Apply(UserExternalIdentityLinkedEvent @event, Person person)
    {
        var newRef = new ExternalIdentityRef(@event.LinkId, @event.LoginProviderId, @event.Issuer);
        person.ExternalIdentities = person.ExternalIdentities
            .Where(r => r.LinkId != @event.LinkId)
            .Append(newRef)
            .ToList();
        return person;
    }

    public Person Apply(UserExternalIdentityUnlinkedEvent @event, Person person)
    {
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
