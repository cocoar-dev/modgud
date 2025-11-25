using Cocoar.Auth.Domain.Entities;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository interface for user operations.
/// </summary>
public interface IUserRepository
{
    Task<ApplicationUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApplicationUser?> GetByUserNameAsync(string normalizedUserName, CancellationToken cancellationToken = default);
    Task<ApplicationUser?> GetByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default);
    Task<List<ApplicationUser>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<(List<ApplicationUser> Users, int TotalCount)> GetPagedAsync(int page, int pageSize, string? search = null, CancellationToken cancellationToken = default);
    Task<List<ApplicationUser>> GetByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default);
    Task CreateAsync(ApplicationUser user, CancellationToken cancellationToken = default);
    Task UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default);
    Task DeleteAsync(ApplicationUser user, CancellationToken cancellationToken = default);
}
