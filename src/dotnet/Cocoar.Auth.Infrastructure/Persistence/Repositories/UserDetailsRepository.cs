using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Application.Models;
using Marten;

namespace Cocoar.Auth.Infrastructure.Persistence.Repositories;

/// <summary>
/// Repository for querying denormalized user details from the async projection.
/// </summary>
public class UserDetailsRepository : IUserDetailsRepository
{
    private readonly IDocumentSession _session;

    public UserDetailsRepository(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<UserDetailsReadModel?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await _session.LoadAsync<UserDetailsReadModel>(id, cancellationToken);
        return user?.IsDeleted == true ? null : user;
    }

    public async Task<(List<UserDetailsReadModel> Users, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var baseQuery = _session.Query<UserDetailsReadModel>()
            .Where(u => !u.IsDeleted); // Filter out deleted users

        Marten.Linq.IMartenQueryable<UserDetailsReadModel> query;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchLower = search.ToLowerInvariant();
            query = (Marten.Linq.IMartenQueryable<UserDetailsReadModel>)baseQuery.Where(u =>
                (u.Email != null && u.Email.ToLowerInvariant().Contains(searchLower)) ||
                u.UserName.ToLowerInvariant().Contains(searchLower) ||
                (u.FirstName != null && u.FirstName.ToLowerInvariant().Contains(searchLower)) ||
                (u.LastName != null && u.LastName.ToLowerInvariant().Contains(searchLower)));
        }
        else
        {
            query = (Marten.Linq.IMartenQueryable<UserDetailsReadModel>)baseQuery;
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var usersResult = await query
            .OrderBy(u => u.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var users = usersResult.ToList();

        return (users, totalCount);
    }

    public async Task<List<UserDetailsReadModel>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = await _session.Query<UserDetailsReadModel>()
            .Where(u => !u.IsDeleted)  // Filter out deleted users
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);
        return result.ToList();
    }

    public async Task<List<UserDetailsReadModel>> GetByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var result = await _session.Query<UserDetailsReadModel>()
            .Where(u => u.Roles.Any(r => r.Id == roleId) && !u.IsDeleted)  // Filter out deleted users
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);
        return result.ToList();
    }
}
