using ErrorOr;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Gdpr;
using Modgud.Infrastructure.Persistence.Marten.Mappers;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Application.DTOs.User;

namespace Modgud.Api.Features.Users.Queries;

public record GetUserByIdQuery(Guid UserId);

public class GetUserByIdHandler(IDocumentSession session)
{
    public async Task<ErrorOr<UserDto>> Handle(
        GetUserByIdQuery query,
        CancellationToken ct)
    {
        var user = await session.LoadAsync<UserView>(query.UserId, ct);
        if (user is null || user.IsDeleted)
            return Error.NotFound("User.NotFound", "User not found");

        var dto = user.ToDto();

        // EmailConfirmed lives on the ApplicationUser doc — join in.
        var appUser = await session.LoadAsync<ApplicationUser>(query.UserId, ct);
        dto.EmailConfirmed = appUser?.EmailConfirmed ?? false;

        // Pending-deletion state (recycle bin / self-service grace) — join in so
        // the detail view can badge + freeze the user, mirroring the grid.
        var deletion = await session.LoadAsync<UserDeletionState>(query.UserId, ct);
        if (deletion?.IsDeletionPending == true)
        {
            dto.IsDeletionPending = true;
            dto.DeletionInitiator = deletion.DeletionInitiator?.ToString();
            dto.DeletionDeadline = deletion.DeletionConfirmationDeadline;
        }

        return dto;
    }
}
