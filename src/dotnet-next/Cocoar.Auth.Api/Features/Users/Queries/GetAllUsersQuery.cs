using ErrorOr;
using Marten;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Mappers;
using Cocoar.Auth.Infrastructure.Persistence.Marten.Projections.Users;
using Cocoar.Auth.Application.DTOs.User;

namespace Cocoar.Auth.Api.Features.Users.Queries;

public record GetAllUsersQuery(int? Skip = null, int? Take = null);

public class GetAllUsersHandler(IDocumentSession session)
{
    public async Task<ErrorOr<List<UserDto>>> Handle(
        GetAllUsersQuery query,
        CancellationToken ct)
    {
        IEnumerable<UserView> users = await session.Query<UserView>()
            .Where(u => !u.IsDeleted)
            .ToListAsync(ct);

        users = users.OrderBy(u => u.UserName);

        if (query.Skip.HasValue)
            users = users.Skip(query.Skip.Value);
        if (query.Take.HasValue)
            users = users.Take(query.Take.Value);

        // View is fully denormalized — no manual joining needed
        return users.Select(u => u.ToDto()).ToList();
    }
}
