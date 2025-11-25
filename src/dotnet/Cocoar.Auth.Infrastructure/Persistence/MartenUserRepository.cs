using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence;

public class MartenUserRepository : IUserRepository
{
    private readonly IDocumentSession _session;

    public MartenUserRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<ApplicationUser?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _session.LoadAsync<ApplicationUser>(id, cancellationToken);
    }

    public async Task<ApplicationUser?> GetByUserNameAsync(string normalizedUserName, CancellationToken cancellationToken = default)
    {
        return await _session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);
    }

    public async Task<ApplicationUser?> GetByEmailAsync(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        return await _session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
    }

    public async Task<List<ApplicationUser>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = await _session.Query<ApplicationUser>()
            .ToListAsync(cancellationToken);
        return result.ToList();
    }

    public async Task<(List<ApplicationUser> Users, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ApplicationUser> query = _session.Query<ApplicationUser>();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchUpper = search.ToUpperInvariant();
            query = query.Where(u =>
                u.NormalizedUserName.Contains(searchUpper) ||
                (u.NormalizedEmail != null && u.NormalizedEmail.Contains(searchUpper)) ||
                (u.FirstName != null && u.FirstName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (u.LastName != null && u.LastName.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var users = await query
            .OrderBy(u => u.UserName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (users.ToList(), totalCount);
    }

    public async Task<List<ApplicationUser>> GetByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var result = await _session.Query<ApplicationUser>()
            .Where(u => u.Roles.Contains(roleId))
            .ToListAsync(cancellationToken);
        return result.ToList();
    }

    public async Task CreateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        _session.Store(user);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        _session.Store(user);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        _session.Delete(user);
        await _session.SaveChangesAsync(cancellationToken);
    }
}
