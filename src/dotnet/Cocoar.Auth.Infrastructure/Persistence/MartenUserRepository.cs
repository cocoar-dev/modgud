using Cocoar.Auth.Application.Interfaces;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Infrastructure.Persistence.Projections;
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
        // Query from UserState projection for better performance
        var states = await _session.Query<UserState>()
            .Where(u => !u.IsDeleted)
            .ToListAsync(cancellationToken);

        return states.Select(MapToApplicationUser).ToList();
    }

    public async Task<(List<ApplicationUser> Users, int TotalCount)> GetPagedAsync(
        int page,
        int pageSize,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        // Query from UserState projection for better performance
        IQueryable<UserState> query = _session.Query<UserState>()
            .Where(u => !u.IsDeleted);

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
        var readModels = await query
            .OrderBy(u => u.UserName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (readModels.Select(MapToApplicationUser).ToList(), totalCount);
    }

    public async Task<List<ApplicationUser>> GetByRoleIdAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        // Query from UserState projection for better performance
        var states = await _session.Query<UserState>()
            .Where(u => !u.IsDeleted && u.Roles.Contains(roleId))
            .ToListAsync(cancellationToken);

        return states.Select(MapToApplicationUser).ToList();
    }

    /// <summary>
    /// Maps a UserState to ApplicationUser for backward compatibility.
    /// Note: Security-sensitive fields (PasswordHash, SecurityStamp, etc.) are NOT included.
    /// </summary>
    private static ApplicationUser MapToApplicationUser(UserState state)
    {
        var user = new ApplicationUser(state.UserName, state.Email);

        // Use reflection or internal methods to set properties
        // For now, create a new user and copy properties
        user.SetPhoneNumber(state.PhoneNumber);
        user.SetFirstName(state.FirstName);
        user.SetLastName(state.LastName);
        user.SetIsActive(state.IsActive);
        user.SetLockoutEnabled(state.LockoutEnabled);
        user.SetLockoutEnd(state.LockoutEnd);
        user.SetTwoFactorEnabled(state.TwoFactorEnabled);
        user.SetEmailConfirmed(state.EmailConfirmed);
        user.SetPhoneNumberConfirmed(state.PhoneNumberConfirmed);

        foreach (var roleId in state.Roles)
        {
            user.AddRole(roleId);
        }

        // Set the ID using reflection since it's protected
        typeof(ApplicationUser).BaseType!
            .GetProperty("Id")!
            .SetValue(user, state.Id);

        typeof(ApplicationUser).BaseType!
            .GetProperty("CreatedAt")!
            .SetValue(user, state.CreatedAt);

        if (state.ModifiedAt.HasValue)
        {
            typeof(ApplicationUser).BaseType!
                .GetProperty("ModifiedAt")!
                .SetValue(user, state.ModifiedAt);
        }

        return user;
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
