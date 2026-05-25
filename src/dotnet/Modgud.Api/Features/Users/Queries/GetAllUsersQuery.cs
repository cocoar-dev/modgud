using ErrorOr;
using Marten;
using Modgud.Authentication.Domain;
using Modgud.Infrastructure.Persistence.Marten.Mappers;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;
using Modgud.Application.DTOs.User;

namespace Modgud.Api.Features.Users.Queries;

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

        var page = users.ToList();

        // EmailConfirmed lives on the ApplicationUser doc (Identity-side, not
        // tracked by the read-projection). Batch-load just the confirmation
        // flag for the page and merge into the DTOs.
        var confirmedById = await LoadEmailConfirmedAsync(page.Select(u => u.Id), ct);

        return page.Select(u =>
        {
            var dto = u.ToDto();
            dto.EmailConfirmed = confirmedById.TryGetValue(u.Id, out var c) && c;
            return dto;
        }).ToList();
    }

    private async Task<Dictionary<Guid, bool>> LoadEmailConfirmedAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return [];
        var rows = await session.Query<ApplicationUser>()
            .Where(u => idList.Contains(u.Id))
            .Select(u => new { u.Id, u.EmailConfirmed })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.EmailConfirmed);
    }
}
