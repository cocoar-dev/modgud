using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence;

public class MartenRoleRepository : IRoleRepository
{
    private readonly IDocumentSession _session;

    public MartenRoleRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<ApplicationRole?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var role = await _session.LoadAsync<ApplicationRole>(id, cancellationToken);
        return role is { IsDeleted: false } ? role : null;
    }

    public async Task<ApplicationRole?> GetByNameAsync(string normalizedName, CancellationToken cancellationToken = default)
    {
        return await _session.Query<ApplicationRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedName && !r.IsDeleted, cancellationToken);
    }

    public async Task<List<ApplicationRole>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var roles = await _session.Query<ApplicationRole>()
            .Where(r => !r.IsDeleted)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return roles.ToList();
    }

    public async Task<List<ApplicationRole>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        var roles = await _session.Query<ApplicationRole>()
            .Where(r => idList.Contains(r.Id) && !r.IsDeleted)
            .ToListAsync(cancellationToken);
        return roles.ToList();
    }

    public async Task CreateAsync(ApplicationRole role, CancellationToken cancellationToken = default)
    {
        _session.Store(role);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ApplicationRole role, CancellationToken cancellationToken = default)
    {
        _session.Store(role);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(ApplicationRole role, CancellationToken cancellationToken = default)
    {
        _session.Delete(role);
        await _session.SaveChangesAsync(cancellationToken);
    }
}
