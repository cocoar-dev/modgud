using Cocoar.Auth.Authorization.Principals;
using ErrorOr;
using Marten;
using Cocoar.Auth.Application.DTOs.User;
using Cocoar.Auth.Domain.Common;
using Cocoar.Auth.Domain.Errors;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Events;
using Cocoar.Auth.Authentication.Api.Users;
using Cocoar.Auth.Domain.Users.Events;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Mappers;

namespace Cocoar.Auth.Api.Features.Users.Commands;

public class UpdateUserHandler(IDocumentSession session)
{
    public async Task<ErrorOr<UserDto>> Handle(
        UpdateUserCommand command,
        CancellationToken ct)
    {
        // Validation via the polymorphic Principal projection (inline, always consistent)
        var person = await session.LoadAsync<Person>(command.UserId, ct);
        if (person is null || person.IsDeleted)
            return Error.NotFound("User.NotFound", "User not found");

        // Check UserName uniqueness (humans only, exclude current user)
        string? normalizedUserName = null;
        if (command.UserName.HasValue && command.UserName.Value is { } rawUserName)
        {
            normalizedUserName = rawUserName.Trim().ToLowerInvariant();
            var userNameTaken = await session.Query<Person>()
                .Where(p => p.AccountName == normalizedUserName
                         && p.Id != command.UserId
                         && !p.IsDeleted)
                .AnyAsync(ct);
            if (userNameTaken)
                return DomainErrors.User.UserNameTaken(normalizedUserName);
        }

        // Email is required on humans — reject attempts to clear it. The
        // ServiceAccount Principal kind carves out emailless machine
        // identities; this endpoint only mutates Person+ApplicationUser.
        if (command.Email.HasValue && string.IsNullOrWhiteSpace(command.Email.Value))
            return DomainErrors.User.EmailRequired;

        // Check Email uniqueness (exclude current user). Groups have their own Email
        // field — check both to prevent collisions across principal kinds.
        if (command.Email.HasValue && !string.IsNullOrWhiteSpace(command.Email.Value))
        {
            var email = command.Email.Value;
            var personEmailTaken = await session.Query<Person>()
                .Where(p => p.Email == email && p.Id != command.UserId && !p.IsDeleted)
                .AnyAsync(ct);
            if (personEmailTaken)
                return DomainErrors.User.EmailTaken(email);

            var groupEmailTaken = await session.Query<Group>()
                .Where(g => g.Email == email && !g.IsDeleted)
                .AnyAsync(ct);
            if (groupEmailTaken)
                return DomainErrors.User.EmailTaken(email);
        }

        var @event = new UserUpdatedEvent(
            command.UserId,
            Firstname: command.Firstname,
            Lastname: command.Lastname,
            Acronym: command.Acronym,
            Email: command.Email
        );

        session.Events.Append(command.UserId, @event);

        // Sync ALL profile changes to ApplicationUser (Identity document)
        {
            var appUser = await session.LoadAsync<ApplicationUser>(command.UserId, ct);
            if (appUser is not null)
            {
                if (command.UserName.HasValue)
                {
                    appUser.UserName = normalizedUserName!;
                    appUser.NormalizedUserName = normalizedUserName!.ToUpperInvariant();
                }
                if (command.Email.HasValue)
                {
                    appUser.Email = command.Email.Value;
                    appUser.NormalizedEmail = command.Email.Value?.ToUpperInvariant();
                }
                if (command.Firstname.HasValue) appUser.Firstname = command.Firstname.Value;
                if (command.Lastname.HasValue) appUser.Lastname = command.Lastname.Value;
                if (command.Acronym.HasValue) appUser.Acronym = command.Acronym.Value;
                session.Store(appUser);
            }

            if (command.UserName.HasValue)
            {
                session.Events.Append(command.UserId,
                    new UserUserNameChangedEvent(command.UserId, normalizedUserName!));
            }
        }

        // Label sync for TodoViews/CommentViews is handled by ReferenceSyncHandlers
        // via Marten Event Forwarding (UserUpdatedEvent → Wolverine → sync handlers)

        await session.SaveChangesAsync(ct);

        return new UserDto
        {
            Id = new BuildingBlocks.Helper.ShortGuid(command.UserId).ToString(),
            Firstname = command.Firstname.HasValue ? command.Firstname.Value : person.Firstname ?? string.Empty,
            Lastname = command.Lastname.HasValue ? command.Lastname.Value : person.Lastname ?? string.Empty,
            Acronym = command.Acronym.HasValue ? command.Acronym.Value : person.Acronym,
            Email = command.Email.HasValue ? command.Email.Value : person.Email,
            UserName = normalizedUserName ?? person.AccountName,
            IsActive = person.IsActive,
        };
    }
}
