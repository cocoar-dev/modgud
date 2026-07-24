using BuildingBlocks.Helper;
using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Application.DTOs.User;
using Modgud.Domain.Errors;
using Modgud.Domain.Realms;
using Modgud.Authentication.Events;
using Modgud.Authentication.Applications;
using Modgud.Authentication.Domain;
using Modgud.Authorization.Principals;


namespace Modgud.Api.Features.Users.Commands;

public record CreateUserCommand(string? Firstname, string? Lastname, string? Acronym, string? Email, string UserName, string? Password, bool EmailConfirmed = false, bool IsActive = true);

public class CreateUserHandler(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    IApplicationSettingsResolver settingsResolver)
{
    public async Task<ErrorOr<UserDto>> Handle(
        CreateUserCommand command,
        CancellationToken ct)
    {
        // Email is required on humans (the always-required anchor). The
        // Authorization model carves out ServiceAccount as a separate Principal
        // kind for emailless machine identities — this endpoint only creates
        // Person+ApplicationUser, which always represents a human.
        if (string.IsNullOrWhiteSpace(command.Email))
            return DomainErrors.User.EmailRequired;

        if (!IsValidEmail(command.Email))
            return DomainErrors.User.EmailInvalid;

        // Configurable (App⊕realm) identity-field policy. Default = all Optional
        // (today's behaviour): username defaults to the email, names may be blank.
        var registrationFields = (await settingsResolver.ResolveForCurrentRequestAsync(ct)).RegistrationFields;
        if (RegistrationFieldsPolicy.FirstMissingRequired(
                registrationFields, command.UserName, command.Firstname, command.Lastname) is { } missing)
        {
            return missing switch
            {
                RegistrationField.Firstname => DomainErrors.User.FirstnameRequired,
                RegistrationField.Lastname => DomainErrors.User.LastnameRequired,
                _ => DomainErrors.User.UserNameRequired,
            };
        }

        // Username: Off → always the email; Optional/blank → the email; else the
        // supplied value (validated non-empty above when Required).
        var normalizedUserName = RegistrationFieldsPolicy
            .ResolveUsername(registrationFields, command.UserName, command.Email)
            .ToLowerInvariant();

        // Check UserName uniqueness (humans only)
        var userNameTaken = await session.Query<Person>()
            .Where(p => p.AccountName == normalizedUserName && !p.IsDeleted)
            .AnyAsync(ct);
        if (userNameTaken)
            return DomainErrors.User.UserNameTaken(normalizedUserName);

        // Check Email uniqueness — persons carry emails directly, groups have
        // their own Email field but we care about collisions across both.
        if (!string.IsNullOrWhiteSpace(command.Email))
        {
            var normalizedEmail = command.Email.ToUpperInvariant();
            var personEmailTaken = await session.Query<Person>()
                .Where(p => p.NormalizedEmail == normalizedEmail && !p.IsDeleted)
                .AnyAsync(ct);
            if (personEmailTaken)
                return DomainErrors.User.EmailTaken(command.Email);

            var groupEmailTaken = await session.Query<Group>()
                .Where(g => g.Email != null && g.Email.ToUpper() == normalizedEmail && !g.IsDeleted)
                .AnyAsync(ct);
            if (groupEmailTaken)
                return DomainErrors.User.EmailTaken(command.Email);
        }

        var id = Guid.NewGuid();
        var hasPassword = false;

        var appUser = new ApplicationUser(normalizedUserName, command.Email)
        {
            Id = id,
            Firstname = command.Firstname,
            Lastname = command.Lastname,
            Acronym = command.Acronym,
            IsActive = command.IsActive,
            EmailConfirmed = command.EmailConfirmed,
        };

        // Store handles event stream creation (StartStream + UserCreatedEvent + UserUserNameChangedEvent)
        // and document persistence (ApplicationUser + UserSecurityData)
        IdentityResult createResult;
        if (!string.IsNullOrWhiteSpace(command.Password))
        {
            createResult = await userManager.CreateAsync(appUser, command.Password);
            hasPassword = createResult.Succeeded;
        }
        else
        {
            createResult = await userManager.CreateAsync(appUser);
        }

        if (!createResult.Succeeded)
        {
            return Error.Validation("User.IdentityError",
                string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        // The read model takes IsActive exclusively from UserActivatedEvent /
        // UserDeactivatedEvent — UserCreatedEvent carries no such field and
        // UserView defaults to active. Writing it on the ApplicationUser
        // document alone would leave the grid, the list query and everything
        // else projection-driven claiming the user is active. Append the same
        // event the update path appends, so "created inactive" is one honest
        // stream rather than a document that disagrees with its projection.
        if (!command.IsActive)
        {
            session.Events.Append(id, new UserDeactivatedEvent(id));
            await session.SaveChangesAsync(ct);
        }

        return new UserDto
        {
            Id = new ShortGuid(id).ToString(),
            Firstname = command.Firstname,
            Lastname = command.Lastname,
            Acronym = command.Acronym,
            Email = command.Email,
            UserName = normalizedUserName,
            IsActive = command.IsActive,
            HasPassword = hasPassword,
            EmailConfirmed = command.EmailConfirmed,
        };
    }

    // Email format guard — mirrors the SPA's client-side check. Deliberately
    // simple (non-empty local part, "@", non-empty domain containing a dot):
    // enough to reject the obvious garbage (e.g. "notanemail") that previously
    // persisted unchallenged, without over-fitting RFC 5322 edge cases.
    private static bool IsValidEmail(string email) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}
