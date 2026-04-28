using System.Security.Claims;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Infrastructure.Identity;

/// <summary>
/// Marten-based implementation of IRoleStore that supports event sourcing.
/// Appends domain events for auditable changes.
/// </summary>
public class EventSourcedRoleStore :
    IRoleStore<ApplicationRole>,
    IRoleClaimStore<ApplicationRole>,
    IQueryableRoleStore<ApplicationRole>
{
    private readonly IDocumentSession _session;

    public EventSourcedRoleStore(IDocumentSession session)
    {
        _session = session;
    }

    public IQueryable<ApplicationRole> Roles => _session.Query<ApplicationRole>().Where(r => !r.IsDeleted);

    #region IRoleStore

    public async Task<IdentityResult> CreateAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);

        // Discard events raised during construction — RoleCreated covers initial state
        role.ClearPendingEvents();

        var @event = new RoleCreated(
            role.Id,
            role.Name,
            role.Description,
            role.ClientId);

        _session.Events.StartStream<Domain.Aggregates.RoleAggregate>(role.Id, @event);

        // Store ApplicationRole for backward compatibility
        _session.Store(role);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);

        // Append pending domain events raised by entity mutations
        if (role.PendingEvents.Count > 0)
        {
            _session.Events.Append(role.Id, role.PendingEvents.ToArray());
            role.ClearPendingEvents();
        }

        role.SetConcurrencyStamp(Guid.NewGuid().ToString());
        _session.Eject(role);
        _session.Store(role);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);

        // Append delete event for event sourcing / projection rebuild
        _session.Events.Append(role.Id, new RoleDeleted(role.Id, null));

        role.MarkDeleted();
        _session.Eject(role);
        _session.Store(role);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<ApplicationRole?> FindByIdAsync(string roleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(roleId, out var id))
            return null;

        var role = await _session.LoadAsync<ApplicationRole>(id, cancellationToken);
        return role is { IsDeleted: false } ? role : null;
    }

    public async Task<ApplicationRole?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _session.Query<ApplicationRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName && !r.IsDeleted, cancellationToken);
    }

    public Task<string?> GetNormalizedRoleNameAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        return Task.FromResult<string?>(role.NormalizedName);
    }

    public Task<string> GetRoleIdAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        return Task.FromResult(role.Id.ToString());
    }

    public Task<string?> GetRoleNameAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        return Task.FromResult<string?>(role.Name);
    }

    public Task SetNormalizedRoleNameAsync(ApplicationRole role, string? normalizedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        // NormalizedName is set automatically when SetName is called
        return Task.CompletedTask;
    }

    public Task SetRoleNameAsync(ApplicationRole role, string? roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        if (roleName is not null)
            role.SetName(roleName);
        return Task.CompletedTask;
    }

    #endregion

    #region IRoleClaimStore

    public Task<IList<Claim>> GetClaimsAsync(ApplicationRole role, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        var claims = role.Claims.Select(c => new Claim(c.Type, c.Value)).ToList();
        return Task.FromResult<IList<Claim>>(claims);
    }

    public Task AddClaimAsync(ApplicationRole role, Claim claim, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        role.AddClaim(claim.Type, claim.Value);
        return Task.CompletedTask;
    }

    public Task RemoveClaimAsync(ApplicationRole role, Claim claim, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);
        role.RemoveClaim(claim.Type, claim.Value);
        return Task.CompletedTask;
    }

    #endregion

    public void Dispose()
    {
        // Marten session is managed by DI
    }
}
