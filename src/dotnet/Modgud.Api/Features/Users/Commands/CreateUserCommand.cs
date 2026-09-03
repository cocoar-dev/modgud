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
using Modgud.Authorization.Events;
using Modgud.Authorization.Principals;
using Modgud.Domain.Users.Events;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;


namespace Modgud.Api.Features.Users.Commands;

public record CreateUserCommand(
    string? Firstname,
    string? Lastname,
    string? Acronym,
    string? Email,
    string UserName,
    string? Password,
    bool EmailConfirmed = false,
    bool IsActive = true,
    IReadOnlyList<string>? GroupIds = null,
    int? GracePeriodDaysOverride = null,
    bool TwoFactorExempt = false,
    // Optional pinned entity id — provisioning only (the manifest applier
    // pre-checks stream availability); server-generated when null.
    Guid? Id = null);

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

        // MG-FT-01 — position principals share the account-name namespace: a
        // position's id becomes a token subject exactly like a person's, so a
        // human must not take a position's handle.
        var positionNameTaken = await session.Query<PositionPrincipal>()
            .Where(f => f.AccountName == normalizedUserName && !f.IsDeleted)
            .AnyAsync(ct);
        if (positionNameTaken)
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

        // Resolve and validate every requested membership before creating the
        // user. This keeps the command all-or-nothing: a malformed, missing or
        // automatic group can never leave a bare user behind.
        var groups = new List<Group>();
        foreach (var rawGroupId in command.GroupIds?.Distinct() ?? [])
        {
            if (!ShortGuid.TryParse(rawGroupId, out Guid groupId))
                return Error.Validation("User.InvalidGroupId", $"Group ID '{rawGroupId}' is invalid");

            var group = await session.LoadAsync<Group>(groupId, ct);
            if (group is null || group.IsDeleted)
                return Error.NotFound("User.GroupNotFound", $"Group with ID '{rawGroupId}' was not found");
            if (group.MembershipMode == MembershipMode.Auto)
                return Error.Validation("User.AutoGroupMembership",
                    $"Group '{group.Name}' has automatic membership and cannot receive direct members");

            groups.Add(group);
        }

        var id = command.Id ?? Guid.NewGuid();

        var appUser = new ApplicationUser(normalizedUserName, command.Email)
        {
            Id = id,
            Firstname = command.Firstname,
            Lastname = command.Lastname,
            Acronym = command.Acronym,
            IsActive = command.IsActive,
            EmailConfirmed = command.EmailConfirmed,
        };

        // Run the same configured Identity validators used by UserManager,
        // then stage the Identity documents ourselves. EventSourcedUserStore's
        // CreateAsync commits immediately, which made it impossible to include
        // memberships and the per-user 2FA policy in the same transaction.
        appUser.NormalizedUserName = userManager.NormalizeName(appUser.UserName) ?? string.Empty;
        appUser.NormalizedEmail = userManager.NormalizeEmail(appUser.Email);

        var identityErrors = new List<IdentityError>();
        foreach (var validator in userManager.UserValidators)
        {
            var result = await validator.ValidateAsync(userManager, appUser);
            if (!result.Succeeded) identityErrors.AddRange(result.Errors);
        }

        if (!string.IsNullOrWhiteSpace(command.Password))
        {
            foreach (var validator in userManager.PasswordValidators)
            {
                var result = await validator.ValidateAsync(userManager, appUser, command.Password);
                if (!result.Succeeded) identityErrors.AddRange(result.Errors);
            }
        }
        if (identityErrors.Count > 0)
        {
            return Error.Validation("User.IdentityError",
                string.Join("; ", identityErrors.Select(e => e.Description)));
        }

        if (!string.IsNullOrWhiteSpace(command.Password))
            appUser.PasswordHash = userManager.PasswordHasher.HashPassword(appUser, command.Password);

        var userEvents = new List<object>
        {
            new UserCreatedEvent(id, command.Firstname, command.Lastname, command.Acronym, command.Email),
            new UserUserNameChangedEvent(id, normalizedUserName),
        };
        if (appUser.PasswordHash is not null)
            userEvents.Add(new UserPasswordChangedEvent(id, null));
        if (!command.IsActive)
            userEvents.Add(new UserDeactivatedEvent(id));
        session.Events.StartStream<UserView>(id, userEvents);

        session.Store(appUser);

        var securityData = UserSecurityData.Create(id, appUser.PasswordHash);
        if (!string.IsNullOrEmpty(appUser.SecurityStamp))
            securityData.SecurityStamp = appUser.SecurityStamp;
        securityData.GracePeriodDaysOverride = command.GracePeriodDaysOverride is null
            ? null
            : Math.Max(0, command.GracePeriodDaysOverride.Value);
        securityData.TwoFactorExempt = command.TwoFactorExempt;
        session.Store(securityData);

        foreach (var group in groups)
        {
            session.Events.Append(group.Id, new GroupUpdatedEvent(
                group.Id, group.Name, group.Description,
                group.MemberIds.Append(id).Distinct().ToList(), group.RoleIds,
                group.MembershipMode, group.MembershipScript, group.CompiledMembershipScript,
                group.MembershipScriptDependencies,
                group.Email, group.EmailMode,
                BoundTo: group.BoundTo,
                ExternallyDrivable: group.ExternallyDrivable));
        }

        // One Marten SaveChanges = one PostgreSQL transaction for the complete
        // object: user stream, authentication documents, policy and memberships.
        await session.SaveChangesAsync(ct);

        return new UserDto
        {
            Id = new ShortGuid(id).ToString(),
            Firstname = command.Firstname,
            Lastname = command.Lastname,
            Acronym = command.Acronym,
            Email = command.Email,
            UserName = normalizedUserName,
            IsActive = command.IsActive,
            HasPassword = appUser.PasswordHash is not null,
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
