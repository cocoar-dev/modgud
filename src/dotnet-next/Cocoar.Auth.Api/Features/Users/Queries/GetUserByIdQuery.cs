using ErrorOr;
using Marten;
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

        // View is fully denormalized — no manual joining needed
        return user.ToDto();
    }
}
