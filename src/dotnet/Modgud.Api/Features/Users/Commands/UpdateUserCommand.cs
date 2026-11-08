using Modgud.Authorization.Principals;
using ErrorOr;
using Marten;
using Modgud.Application.DTOs.User;
using Modgud.Domain.Common;
using Modgud.Domain.Errors;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.Gdpr;
using Modgud.Authentication.Sessions;
using Modgud.Authentication.Api.Users;
using Modgud.Authentication.Applications;
using Modgud.Domain.Realms;
using Modgud.Domain.Users.Events;
using Modgud.Infrastructure.FollowUps;
using Modgud.Infrastructure.Persistence.Marten.Mappers;
using Modgud.Infrastructure.Persistence.Tenancy;

namespace Modgud.Api.Features.Users.Commands;

public class UpdateUserHandler(IDocumentSession session)
{
    public async Task<ErrorOr<UserDto>> Handle(
        UpdateUserCommand command,
        IUserAccessRevoker accessRevoker,
        IApplicationSettingsResolver settingsResolver,
        IFollowUpOutbox followUps,
        ILogger<UpdateUserHandler> logger,
        CancellationToken ct)
    {
        // Validation via the polymorphic Principal projection (inline, always consistent)
        var person = await session.LoadAsync<Person>(command.UserId, ct);
        if (person is null || person.IsDeleted)
            return Error.NotFound("User.NotFound", "User not found");

        // Freeze edits while a deletion is pending (self-service grace OR admin
        // recycle bin). The account is read-only until it is restored/cancelled
        // or permanently erased — the only valid mutation is restore.
        var deletionState = await session.LoadAsync<UserDeletionState>(command.UserId, ct);
        if (deletionState?.IsDeletionPending == true)
            return Error.Conflict("User.DeletionPending",
                "This user has a pending deletion and is read-only. Restore the user before editing.");

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

            // MG-FT-01 — position principals share the account-name namespace
            // (see CreateUserCommand for the rationale).
            var positionNameTaken = await session.Query<PositionPrincipal>()
                .Where(f => f.AccountName == normalizedUserName && !f.IsDeleted)
                .AnyAsync(ct);
            if (positionNameTaken)
                return DomainErrors.User.UserNameTaken(normalizedUserName);
        }

        // Email is required on humans — reject attempts to clear it. The
        // ServiceAccount Principal kind carves out emailless machine
        // identities; this endpoint only mutates Person+ApplicationUser.
        if (command.Email.HasValue && string.IsNullOrWhiteSpace(command.Email.Value))
            return DomainErrors.User.EmailRequired;

        // Configurable (App⊕realm) identity-field policy: a field marked Required
        // must not be emptied by an edit (a blank value on a field the caller chose
        // to touch). Pre-existing empties on fields the edit leaves alone are not
        // forced — only an explicit clear of a Required field is rejected.
        var registrationFields = (await settingsResolver.ResolveForCurrentRequestAsync(ct)).RegistrationFields
                                 ?? RegistrationFieldsSettings.Defaults;
        if (command.UserName.HasValue && string.IsNullOrWhiteSpace(command.UserName.Value)
            && registrationFields.Username == FieldRequirement.Required)
            return DomainErrors.User.UserNameRequired;
        if (command.Firstname.HasValue && string.IsNullOrWhiteSpace(command.Firstname.Value)
            && registrationFields.Firstname == FieldRequirement.Required)
            return DomainErrors.User.FirstnameRequired;
        if (command.Lastname.HasValue && string.IsNullOrWhiteSpace(command.Lastname.Value)
            && registrationFields.Lastname == FieldRequirement.Required)
            return DomainErrors.User.LastnameRequired;

        if (command.Email.HasValue && !string.IsNullOrWhiteSpace(command.Email.Value)
            && !IsValidEmail(command.Email.Value!))
            return DomainErrors.User.EmailInvalid;

        // Check Email uniqueness (exclude current user). Groups have their own Email
        // field — check both to prevent collisions across principal kinds.
        if (command.Email.HasValue && !string.IsNullOrWhiteSpace(command.Email.Value))
        {
            var email = command.Email.Value;
            var normalizedEmail = email.ToUpperInvariant();
            var personEmailTaken = await session.Query<Person>()
                .Where(p => p.NormalizedEmail == normalizedEmail && p.Id != command.UserId && !p.IsDeleted)
                .AnyAsync(ct);
            if (personEmailTaken)
                return DomainErrors.User.EmailTaken(email);

            var groupEmailTaken = await session.Query<Group>()
                .Where(g => g.Email != null && g.Email.ToUpper() == normalizedEmail && !g.IsDeleted)
                .AnyAsync(ct);
            if (groupEmailTaken)
                return DomainErrors.User.EmailTaken(email);
        }

        // B4-AP-01 — applying a change request: the request turns Approved in the commit of the
        // user change, never in a later one. It must still be the pending request the caller
        // loaded (a concurrent approval or an edit by the user in between is refused).
        if (command.Approval is { } approval)
        {
            var request = await session.LoadAsync<UserChangeRequest>(approval.RequestId, ct);
            if (request is null || request.UserId != command.UserId
                || request.Status != ChangeRequestStatus.AdminApprovalPending)
                return Error.Conflict("ChangeRequest.NotPending", "The change request is no longer awaiting admin approval.");
            if (Math.Abs((request.UpdatedAt - approval.SeenUpdatedAt).TotalMilliseconds) >= 1)
                return Error.Conflict("ChangeRequest.Changed", "The request changed since you opened it. Reload and review it again.");
            request.Status = ChangeRequestStatus.Approved;
            request.ReviewedAt = DateTimeOffset.UtcNow;
            request.ReviewedByUserId = approval.ReviewedByUserId;
            if (approval.PreviousValues is { } previous)
                request.PreviousValues = new Dictionary<string, string?>(previous);
            session.Store(request);
            foreach (var followUp in approval.FollowUps ?? [])
                await followUps.PublishAsync(session, followUp);
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
        var emailChanged = false;
        {
            var appUser = await session.LoadAsync<ApplicationUser>(command.UserId, ct);
            if (appUser is not null)
            {
                if (command.UserName.HasValue)
                {
                    appUser.UserName = normalizedUserName!;
                    appUser.NormalizedUserName = normalizedUserName!.ToUpperInvariant();
                }
                if (command.Email.HasValue
                    && !string.Equals(appUser.Email, command.Email.Value, StringComparison.OrdinalIgnoreCase))
                {
                    emailChanged = true;
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

        // Audit remediation #4: email is the account-recovery anchor (controls future
        // forgot-password + magic-link delivery). This path mutates it via raw
        // session.Store, bypassing UserManager — so even Identity's implicit stamp
        // rotation is lost and existing tokens/sessions survive. Revoke live access
        // when the email actually changes. B4-AP-01 — the revoke is owed from the commit
        // on: it is written with it, so a failure afterwards is retried, not lost (a
        // repeated update sees the new email as unchanged and would revoke nothing).
        // Inside a manifest apply SaveChanges does not commit (the shared transaction does,
        // later): there the revoke below is deferred until after that commit instead.
        // A safety net for the immediate revoke below: delivered once that has finished, so the
        // two do not end the same user's access at the same moment.
        if (emailChanged && TenantApplyTransaction.Current is null)
            await followUps.PublishSafetyNetAsync(session,
                new RevokeUserAccess(command.UserId, AccessRevocationReason.ForceSignOut));

        await session.SaveChangesAsync(ct);

        // Revoke at once as well, so the old access ends before the response; a failure here
        // no longer fails the committed update — the follow-up repeats the revoke until it held.
        if (emailChanged)
        {
            try
            {
                await accessRevoker.RevokeAllAccessAsync(command.UserId, AccessRevocationReason.ForceSignOut, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Revoking the access of user {UserId} after an email change failed; the follow-up retries it",
                    command.UserId);
            }
        }

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

    // Email format guard — same rule as CreateUserHandler / the SPA. Simple by
    // design (local "@" domain-with-dot); rejects obvious garbage without
    // over-fitting RFC 5322.
    private static bool IsValidEmail(string email) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");
}
