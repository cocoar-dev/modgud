using ErrorOr;
using Marten;
using Cocoar.Auth.Authentication.Domain;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Mappers;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;
using Cocoar.Auth.Application.DTOs.User;

namespace Cocoar.Auth.Api.Features.Users.Queries;

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

        return dto;
    }
}
