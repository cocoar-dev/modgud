using ErrorOr;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Authentication.Gdpr;
using Modgud.Authentication.RealmSettings;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Principals;
using Modgud.Domain.Realms;
using Modgud.Domain.Users.Events;


namespace Modgud.Api.Features.Users.Commands;

/// <summary>
/// Admin "delete user" → moves the user(s) into the recycle bin (Account-
/// Lifecycle plan, WS4). This is a reversible soft state, NOT a permanent
/// erase: the user is deactivated and loses live access, a pending-deletion
/// with an admin-retention deadline is recorded, but <c>IsDeleted</c> stays
/// false and the email stays reserved (so the user can be restored, and the
/// partial unique index keeps the address). Permanent erasure happens later —
/// manually via ForceDelete (the permanent-erase endpoint) or automatically by
/// the retention auto-purge job.
/// </summary>
public record DeleteUsersCommand(List<Guid> UserIds, Guid? RequestedByAdminUserId = null);

public class DeleteUsersHandler(
    IDocumentSession session,
    IUserAccessRevoker accessRevoker,
    Modgud.Infrastructure.FunctionTerminals.IFunctionStaffingRevoker staffingRevoker,
    IRealmSettingsService realmSettings,
    TimeProvider clock)
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteUsersCommand command,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var retentionDays = ((await realmSettings.LoadAsync(ct)).Deletion ?? DeletionSettings.Defaults).AdminRetentionDays;
        var deadline = now.AddDays(retentionDays);

        // Resolve the users that will actually be binned: skip missing, already
        // permanently-erased, and already-pending users (idempotent re-delete).
        var toBin = new List<Guid>();
        foreach (var id in command.UserIds)
        {
            var user = await session.LoadAsync<Person>(id, ct);
            if (user is null || user.IsDeleted)
                continue;
            var state = await session.LoadAsync<UserDeletionState>(id, ct);
            if (state?.IsDeletionPending == true || state?.IsDataMasked == true)
                continue;
            toBin.Add(id);
        }

        // Revoke live access (OAuth grants + sessions + security stamp) BEFORE
        // the deactivation is staged — binning is a deletion intent, so full
        // revocation including consent grants (Reason=Deletion, as wired in
        // Hotfix C) is kept; a restored user re-authorizes apps. The revoker
        // commits its own writes immediately; running it ahead of the batched
        // staging below keeps that batch atomic.
        //
        // TENANCY: the revoker resolves the realm from the ambient context. This
        // command MUST be dispatched in-process (bus.InvokeAsync from an HTTP
        // endpoint) so RealmMiddleware's tenant is still ambient — do NOT move it
        // to durable/background dispatch without making the revoker tenant-explicit.
        foreach (var id in toBin)
        {
            await accessRevoker.RevokeAllAccessAsync(id, AccessRevocationReason.Deletion, ct);
            // MG-FT-07 §15.4 — shifts this person opened on shared terminals
            // end with their account: the function tokens stay valid for OTHER
            // activators, so this must target exactly this user's sessions.
            await staffingRevoker.EndAllForUserAsync(
                id, Modgud.Domain.FunctionTerminals.StaffingSessionEndReason.UserDisabled, ct);
        }

        foreach (var id in toBin)
        {
            // Recycle-bin bookkeeping: pending + admin initiator + retention deadline.
            var state = await session.LoadAsync<UserDeletionState>(id, ct)
                        ?? new UserDeletionState { Id = id };
            state.IsDeletionPending = true;
            state.DeletionInitiator = DeletionInitiator.Admin;
            state.DeletionRequestedByUserId = command.RequestedByAdminUserId;
            state.DeletionRequestedAt = now;
            state.DeletionConfirmationDeadline = deadline;
            state.ReminderSentAt = null;
            session.Store(state);

            // Deactivate so the user cannot log in while binned. Do NOT set
            // IsDeleted and do NOT touch external identity links / claims: the
            // bin is reversible, so links stay intact for a clean restore and
            // are only erased at permanent deletion (PerformPermanentEraseAsync).
            var appUser = await session.LoadAsync<ApplicationUser>(id, ct);
            if (appUser is not null && appUser.IsActive)
            {
                appUser.IsActive = false;
                session.Store(appUser);
                session.Events.Append(id, new UserDeactivatedEvent(id));
            }
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
