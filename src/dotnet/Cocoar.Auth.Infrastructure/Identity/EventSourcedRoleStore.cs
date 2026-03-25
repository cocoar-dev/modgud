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

        // Append RoleCreated event
        var @event = new RoleCreated(
            role.Id,
            role.Name,
            role.Description);

        _session.Events.StartStream<Domain.Aggregates.RoleAggregate>(role.Id, @event);

        // Also store ApplicationRole for backward compatibility
        _session.Store(role);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);

        // Load existing role to detect changes
        var existingRole = await _session.LoadAsync<ApplicationRole>(role.Id, cancellationToken);
        if (existingRole is not null)
        {
            // Append events for changes
            AppendChangeEvents(existingRole, role);
        }

        // Update ApplicationRole for backward compatibility
        role.SetConcurrencyStamp(Guid.NewGuid().ToString());
        _session.Store(role);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    private void AppendChangeEvents(ApplicationRole existing, ApplicationRole updated)
    {
        var events = new List<object>();

        // Name change
        if (existing.Name != updated.Name)
        {
            events.Add(new RoleNameChanged(updated.Id, existing.Name, updated.Name));
        }

        // Description change
        if (existing.Description != updated.Description)
        {
            events.Add(new RoleDescriptionChanged(updated.Id, existing.Description, updated.Description));
        }

        // DisplayName change
        if (existing.DisplayName != updated.DisplayName)
        {
            events.Add(new RoleDisplayNameChanged(updated.Id, existing.DisplayName, updated.DisplayName));
        }

        // Email change
        if (existing.Email != updated.Email)
        {
            events.Add(new RoleEmailChanged(updated.Id, existing.Email, updated.Email));
        }

        // BoundToApi change
        if (existing.BoundToApiId != updated.BoundToApiId)
        {
            events.Add(new RoleBoundToApiChanged(updated.Id, existing.BoundToApiId, updated.BoundToApiId));
        }

        // Claim changes
        var existingClaims = existing.Claims.Select(c => (c.Type, c.Value)).ToHashSet();
        var updatedClaims = updated.Claims.Select(c => (c.Type, c.Value)).ToHashSet();

        var addedClaims = updatedClaims.Except(existingClaims);
        var removedClaims = existingClaims.Except(updatedClaims);

        foreach (var claim in addedClaims)
        {
            events.Add(new RoleClaimAdded(updated.Id, claim.Type, claim.Value));
        }

        foreach (var claim in removedClaims)
        {
            events.Add(new RoleClaimRemoved(updated.Id, claim.Type, claim.Value));
        }

        // Append all events
        if (events.Count > 0)
        {
            _session.Events.Append(updated.Id, events.ToArray());
        }
    }

    public async Task<IdentityResult> DeleteAsync(ApplicationRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(role);

        // Append delete event for event sourcing / projection rebuild
        _session.Events.Append(role.Id, new RoleDeleted(role.Id, null));

        // Soft-delete the document (keeps it for projection rebuilds)
        role.MarkDeleted();
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
