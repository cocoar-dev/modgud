using TimeToDo.Application.DTOs.User;

namespace TimeToDo.Application.Contracts;

/// <summary>
/// Query service for retrieving User DTOs.
/// </summary>
public interface IUserQueryService
{
    Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken ct = default);
    Task<UserDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
}
