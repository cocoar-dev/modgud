using ErrorOr;
using Marten;
using TimeToDo.Infrastructure.Persistence.Marten.Mappers;
using TimeToDo.Infrastructure.Persistence.Marten.Projections.Users;
using TimeToDo.Application.DTOs.User;

namespace TimeToDo.Api.Features.Users.Queries;

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
