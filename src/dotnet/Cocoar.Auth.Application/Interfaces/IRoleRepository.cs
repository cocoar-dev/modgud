using Cocoar.Auth.Domain.Entities;

namespace Cocoar.Auth.Application.Interfaces;

/// <summary>
/// Repository interface for role operations.
/// </summary>
public interface IRoleRepository
{
    Task<ApplicationRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ApplicationRole?> GetByNameAsync(string normalizedName, CancellationToken cancellationToken = default);
    Task<List<ApplicationRole>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<List<ApplicationRole>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default);
    Task CreateAsync(ApplicationRole role, CancellationToken cancellationToken = default);
    Task UpdateAsync(ApplicationRole role, CancellationToken cancellationToken = default);
    Task DeleteAsync(ApplicationRole role, CancellationToken cancellationToken = default);
}
