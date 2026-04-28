using TimeToDo.Domain.Entities;

namespace TimeToDo.Domain.Repositories;

/// <summary>
/// Repository interface for User entity.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Gets a user by its ID.
    /// </summary>
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets all users.
    /// </summary>
    Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves a new user.
    /// </summary>
    Task<User> CreateAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing user.
    /// </summary>
    Task<User> UpdateAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// Deletes multiple users by their IDs.
    /// </summary>
    Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
}
