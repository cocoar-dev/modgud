using ErrorOr;
using Marten;
using Modgud.Authentication.Gdpr;
using Modgud.Authorization.Principals;

namespace Modgud.Api.Features.Users.Commands;

/// <summary>
/// Admin "restore from recycle bin" (Account-Lifecycle plan, WS4): clears a
/// pending deletion and reactivates the user. Works on ANY pending deletion
/// regardless of initiator — the support escape hatch — so an admin can also
/// abort a user's self-service deletion. Reactivation only applies to users
/// that were deactivated into the admin recycle bin; a self-service pending
/// user stays active either way. Delegates to <see cref="IGdprService"/> so the
/// cancel/reactivate logic lives in one place.
/// </summary>
public record RestoreUsersCommand(List<Guid> UserIds, Guid? RequestedByAdminUserId = null);

public class RestoreUsersHandler(IDocumentSession session, IGdprService gdpr)
{
    public async Task<ErrorOr<Success>> Handle(RestoreUsersCommand command, CancellationToken ct)
    {
        foreach (var id in command.UserIds)
        {
            var user = await session.LoadAsync<Person>(id, ct);
            if (user is null || user.IsDeleted)
                continue;

            // Admin cancel reactivates an admin-binned user and aborts a
            // self-service grace deletion. A "no pending" result is benign for a
            // bulk restore — skip it rather than failing the whole batch.
            var result = await gdpr.CancelDeletionAsync(id, command.RequestedByAdminUserId ?? Guid.Empty, ct);
            if (result.IsError && result.FirstError.Code != "Gdpr.NoPending")
                return result.FirstError;
        }

        return ErrorOr.Result.Success;
    }
}
