using ErrorOr;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Domain.ExternalAuth;
using Modgud.Authentication.Domain.ExternalAuth.Events;
using Modgud.Authentication.Sessions;
using Modgud.Authorization.Principals;
using Modgud.Domain.Users.Events;


namespace Modgud.Api.Features.Users.Commands;

public record DeleteUsersCommand(List<Guid> UserIds);

public class DeleteUsersHandler(
    IDocumentSession session,
    IUserAccessRevoker accessRevoker,
    TimeProvider clock)
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteUsersCommand command,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // Resolve the users that will actually be deleted (skip missing / already
        // deleted) before touching anything.
        var toDelete = new List<Guid>();
        foreach (var id in command.UserIds)
        {
            var user = await session.LoadAsync<Person>(id, ct);
            if (user is null || user.IsDeleted)
                continue;
            toDelete.Add(id);
        }

        // Revoke live access (OAuth grants + sessions + security stamp) BEFORE the
        // soft-delete is staged. Once IsDeleted flips, UserManager.FindByIdAsync
        // filters the user out and the stamp can no longer be rotated. The revoker
        // commits its own session/stamp writes immediately, so running it ahead of
        // the batched delete-staging below keeps that batch atomic. This is
        // best-effort and non-atomic ACROSS users: a throw on user N leaves
        // users 1..N-1 revoked-but-not-deleted (recoverable, re-revoke idempotent).
        //
        // TENANCY: the revoker's OpenIddict/session/Identity stores resolve the
        // realm from the ambient TenantContext. This command MUST be dispatched
        // in-process (bus.InvokeAsync from an HTTP endpoint) so RealmMiddleware's
        // context is still ambient. Do NOT move it to durable/background dispatch
        // (PublishAsync) without making the revoker tenant-explicit — a background
        // pump has no ambient tenant and would revoke in the system DB.
        foreach (var id in toDelete)
            await accessRevoker.RevokeAllAccessAsync(id, AccessRevocationReason.Deletion, ct);

        foreach (var id in toDelete)
        {
            session.Events.Append(id, new UserDeletedEvent(id));

            // Soft-delete ApplicationUser document so Identity Find methods filter it out
            var appUser = await session.LoadAsync<ApplicationUser>(id, ct);
            if (appUser is not null)
            {
                appUser.IsDeleted = true;
                session.Store(appUser);
            }

            // Federation v1: drop the per-user external-claims snapshot (plain
            // doc, not event-sourced) so externally-derived authz can never
            // outlive the user. Rides this same batched SaveChanges.
            session.Delete<ExternalClaimsStore>(id);

            // Hard-delete any external identity links owned by this user. Soft-
            // unlinking would leave tombstones occupying the (Issuer, Subject)
            // unique-index slot, blocking the same external identity from ever
            // being linked again. Since the user is gone, the link has no
            // meaning to preserve.
            var activeLinks = await session.Query<ExternalIdentityLink>()
                .Where(l => l.UserId == id && !l.IsUnlinked)
                .ToListAsync(ct);
            foreach (var link in activeLinks)
            {
                session.Delete<ExternalIdentityLink>(link.Id);
                session.Events.ArchiveStream(link.Id);
                session.Events.Append(id,
                    new UserExternalIdentityUnlinkedEvent(id, link.Id, link.LoginProviderId, now));
            }
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
