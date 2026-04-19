using Cocoar.Auth.Domain.Authorization.Events;
using Cocoar.Auth.Domain.Events;
using Cocoar.Auth.Domain.Principals;
using Marten.Events.Aggregation;

namespace Cocoar.Auth.Infrastructure.Authorization;

/// <summary>
/// Inline projection feeding <see cref="PrincipalDirectory"/> from events of all
/// principal-producing streams. Today: Person (user-streams) + Group
/// (authorization-group-streams). Future: add Create/Apply overloads for
/// ServiceAccount/Webhook events.
/// <para>
/// Consumes cocoar.auth's existing user events (<c>Cocoar.Auth.Domain.Events.User*</c>)
/// directly — no shim layer. The user event vocabulary stays IDP-essential and is
/// not affected by the ABAC migration.
/// </para>
/// </summary>
public class PrincipalDirectoryProjection : SingleStreamProjection<PrincipalDirectory, Guid>
{
    // ── Person principal (user stream) ─────────────────────────────────

    public PrincipalDirectory Create(UserCreated @event) => new()
    {
        Id = @event.UserId,
        Type = PrincipalType.Person,
        Email = @event.Email,
        NormalizedEmail = @event.Email?.ToUpperInvariant(),
        IsActive = @event.IsActive,
        IsDeleted = false,
        CanAuthenticate = true,
        IsContainer = false,
        Person = new PersonData
        {
            Firstname = @event.FirstName,
            Lastname = @event.LastName,
            UserName = @event.UserName,
            NormalizedUserName = @event.UserName.ToUpperInvariant(),
            PhoneNumber = @event.PhoneNumber,
        },
    };

    public PrincipalDirectory Apply(UserNameChanged @event, PrincipalDirectory current)
        => current with
        {
            Person = (current.Person ?? new PersonData()) with
            {
                UserName = @event.NewUserName,
                NormalizedUserName = @event.NewUserName.ToUpperInvariant(),
            },
        };

    public PrincipalDirectory Apply(UserEmailChanged @event, PrincipalDirectory current)
        => current with
        {
            Email = @event.NewEmail,
            NormalizedEmail = @event.NewEmail?.ToUpperInvariant(),
        };

    public PrincipalDirectory Apply(UserPhoneNumberChanged @event, PrincipalDirectory current)
        => current with
        {
            Person = (current.Person ?? new PersonData()) with
            {
                PhoneNumber = @event.NewPhoneNumber,
            },
        };

    public PrincipalDirectory Apply(UserProfileNameChanged @event, PrincipalDirectory current)
        => current with
        {
            Person = (current.Person ?? new PersonData()) with
            {
                Firstname = @event.NewFirstName,
                Lastname = @event.NewLastName,
            },
        };

    public PrincipalDirectory Apply(UserActivated @event, PrincipalDirectory current)
        => current with { IsActive = true };

    public PrincipalDirectory Apply(UserDeactivated @event, PrincipalDirectory current)
        => current with { IsActive = false };

    public PrincipalDirectory Apply(UserDeleted @event, PrincipalDirectory current)
        => current with { IsDeleted = true, IsActive = false };

    public PrincipalDirectory Apply(UserRestored @event, PrincipalDirectory current)
        => current with { IsDeleted = false, IsActive = true };

    public PrincipalDirectory Apply(UserDataMasked @event, PrincipalDirectory current)
        => current with
        {
            // After GDPR masking, PII is replaced — drop person details to match.
            Email = null,
            NormalizedEmail = null,
            Person = (current.Person ?? new PersonData()) with
            {
                Firstname = "[DELETED]",
                Lastname = "[DELETED]",
                PhoneNumber = null,
            },
        };

    // ── Group principal (authorization-group stream) ───────────────────

    public PrincipalDirectory Create(AuthorizationGroupCreatedEvent @event) => new()
    {
        Id = @event.Id,
        Type = PrincipalType.Group,
        Email = @event.Email,
        NormalizedEmail = @event.Email?.ToUpperInvariant(),
        IsActive = true,
        IsDeleted = false,
        CanAuthenticate = false,
        IsContainer = true,
        Group = new GroupData
        {
            Name = @event.Name,
            EmailMode = @event.EmailMode,
        },
    };

    public PrincipalDirectory Apply(AuthorizationGroupUpdatedEvent @event, PrincipalDirectory current)
        => current with
        {
            Email = @event.Email,
            NormalizedEmail = @event.Email?.ToUpperInvariant(),
            Group = (current.Group ?? new GroupData()) with
            {
                Name = @event.Name,
                EmailMode = @event.EmailMode,
            },
        };

    public PrincipalDirectory Apply(AuthorizationGroupDeletedEvent @event, PrincipalDirectory current)
        => current with { IsDeleted = true, IsActive = false };
}
