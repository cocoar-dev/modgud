using Cocoar.Auth.Application.Models;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository interface for querying denormalized user details (async projection).
/// </summary>
public interface IUserDetailsRepository
{
    /// <summary>
    /// Gets a user with denormalized details by ID.
    /// </summary>
    Task<UserDetailsReadModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a paginated list of users with denormalized details.
    /// </summary>
    Task<(List<UserDetailsReadModel> Users, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        string? search = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all users with denormalized details (use with caution).
    /// </summary>
    Task<List<UserDetailsReadModel>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all users with a specific role.
    /// </summary>
    Task<List<UserDetailsReadModel>> GetByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default);
}
