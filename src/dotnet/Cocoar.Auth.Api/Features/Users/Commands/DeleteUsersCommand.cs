using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Authentication.Domain.ExternalAuth;
using Cocoar.Auth.Authentication.Domain.ExternalAuth.Events;
using Cocoar.Auth.Authorization.Principals;
using Cocoar.Auth.Domain.Users.Events;


namespace Cocoar.Auth.Api.Features.Users.Commands;

public record DeleteUsersCommand(List<Guid> UserIds);

public class DeleteUsersHandler(IDocumentSession session, TimeProvider clock)
{
    public async Task<ErrorOr<Success>> Handle(
        DeleteUsersCommand command,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        foreach (var id in command.UserIds)
        {
            var user = await session.LoadAsync<Person>(id, ct);
            if (user is null || user.IsDeleted)
                continue;

            session.Events.Append(id, new UserDeletedEvent(id));

            // Soft-delete ApplicationUser document so Identity Find methods filter it out
            var appUser = await session.LoadAsync<ApplicationUser>(id, ct);
            if (appUser is not null)
            {
                appUser.IsDeleted = true;
                session.Store(appUser);
            }

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
                    new UserExternalIdentityUnlinkedEvent(id, link.Id, link.IdpConfigId, now));
            }
        }

        await session.SaveChangesAsync(ct);

        return ErrorOr.Result.Success;
    }
}
