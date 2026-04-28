using BuildingBlocks.Helper;
using Marten;
using TimeToDo.Application.Contracts;
using TimeToDo.Application.DTOs.User;
using TimeToDo.Infrastructure.Persistence.Marten.Documents;

namespace TimeToDo.Infrastructure.QueryServices;

public class MartenUserQueryService(IDocumentSession session) : IUserQueryService
{
    public async Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct = default)
    {
        var users = await session.Query<UserDocument>()
            .ToListAsync(ct);

        return users.Select(ToDto).ToList();
    }

    public async Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var user = await session.LoadAsync<UserDocument>(id, ct);
        return user != null ? ToDto(user) : null;
    }

    private static UserDto ToDto(UserDocument document)
    {
        return new UserDto
        {
            Id = new ShortGuid(document.Id).ToString(),
            Firstname = document.Firstname,
            Lastname = document.Lastname,
            Acronym = document.Acronym,
            Email = document.Email
        };
    }
}
