using ErrorOr;
using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Features.Users.Commands;

/// <summary>
/// The single canonical path for an admin setting/resetting a user's password, shared by
/// <c>UsersEndpoints</c> (PUT /api/user/{id}/password) and the realm-provisioning applier
/// so the manual path and the manifest path can't diverge. Mirrors the legacy inline
/// endpoint exactly: (re)set the Identity password, emit <see cref="UserPasswordChangedEvent"/>,
/// then revoke the target's live access (audit remediation #2 — a reset must cut OAuth
/// tokens + device sessions, not just rotate the stamp). The injected
/// <see cref="IDocumentSession"/> is tenant-scoped, so this lands in the active realm.
/// </summary>
public sealed class SetUserPasswordHandler(
    IDocumentSession session,
    UserManager<ApplicationUser> userManager,
    IUserAccessRevoker accessRevoker)
{
    public async Task<ErrorOr<Success>> Handle(Guid userId, string password, CancellationToken ct = default)
    {
        var person = await session.LoadAsync<Person>(userId, ct);
        if (person is null || person.IsDeleted)
            return Error.NotFound("User.NotFound", "User not found");

        var appUser = await userManager.FindByIdAsync(userId.ToString());
        if (appUser is null)
        {
            // No ApplicationUser yet (e.g. a passwordless user) — create it with the password.
            appUser = new ApplicationUser(person.AccountName ?? person.Acronym ?? person.Id.ToString(), person.Email)
            {
                Id = person.Id,
                Firstname = person.Firstname,
                Lastname = person.Lastname,
                Acronym = person.Acronym,
                IsActive = person.IsActive,
            };
            var createResult = await userManager.CreateAsync(appUser, password);
            if (!createResult.Succeeded)
                return Error.Validation("User.PasswordError",
                    string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }
        else
        {
            await userManager.RemovePasswordAsync(appUser);
            var addResult = await userManager.AddPasswordAsync(appUser, password);
            if (!addResult.Succeeded)
                return Error.Validation("User.PasswordError",
                    string.Join("; ", addResult.Errors.Select(e => e.Description)));
        }

        session.Events.Append(userId, new UserPasswordChangedEvent(userId, null));
        await session.SaveChangesAsync(ct);

        // A password reset is an incident-response lever — kill OAuth tokens + device
        // sessions, not just rotate the stamp. No ct: a kill switch must run to completion.
        await accessRevoker.RevokeAllAccessAsync(userId, AccessRevocationReason.ForceSignOut);

        return Result.Success;
    }
}
