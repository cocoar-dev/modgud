using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

// ═══════════════════════════════════════════════════════════════════════════
// INLINE STATE PROJECTION: NORMALIZED ROLE STATE
// ═══════════════════════════════════════════════════════════════════════════
// Naming Convention: *State = Inline projection, single source of truth
// Use for: validation, uniqueness checks, role lookups, Identity stores
// DO NOT use for: API responses, UI display (use async projections instead)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Normalized state model for role data, projected from the event stream.
/// This provides fast query access to role information for validation and Identity.
/// </summary>
public class RoleState
{
    /// <summary>
    /// The unique identifier for this role.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The name of the role.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The normalized (uppercase) name for lookups.
    /// </summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>
    /// A description of the role.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether this role has been deleted (soft delete).
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The claims assigned to this role.
    /// </summary>
    public List<RoleClaim> Claims { get; set; } = [];

    /// <summary>
    /// When this role was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this role was last modified.
    /// </summary>
    public DateTimeOffset? ModifiedAt { get; set; }
}

/// <summary>
/// Inline event-based projection that maintains <see cref="RoleState"/> documents from role events.
/// Runs synchronously with writes for immediate consistency.
/// </summary>
public class RoleStateProjection : EventProjection
{
    /// <summary>
    /// Create a new state model when a role is created.
    /// </summary>
    public RoleState Create(IEvent<RoleCreated> @event)
    {
        var data = @event.Data;
        return new RoleState
        {
            Id = @event.StreamId,
            Name = data.Name,
            NormalizedName = data.Name.ToUpperInvariant(),
            Description = data.Description,
            CreatedAt = @event.Timestamp
        };
    }

    public void Project(IEvent<RoleNameChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<RoleState>(@event.Data.RoleId).GetAwaiter().GetResult();
        if (model is null) return;

        model.Name = @event.Data.NewName;
        model.NormalizedName = @event.Data.NewName.ToUpperInvariant();
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<RoleDescriptionChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<RoleState>(@event.Data.RoleId).GetAwaiter().GetResult();
        if (model is null) return;

        model.Description = @event.Data.NewDescription;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<RoleDeleted> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<RoleState>(@event.Data.RoleId).GetAwaiter().GetResult();
        if (model is null) return;

        model.IsDeleted = true;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<RoleClaimAdded> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<RoleState>(@event.Data.RoleId).GetAwaiter().GetResult();
        if (model is null) return;

        var claim = new RoleClaim(@event.Data.ClaimType, @event.Data.ClaimValue);
        if (!model.Claims.Any(c => c.Type == claim.Type && c.Value == claim.Value))
        {
            model.Claims.Add(claim);
        }
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<RoleClaimRemoved> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<RoleState>(@event.Data.RoleId).GetAwaiter().GetResult();
        if (model is null) return;

        model.Claims.RemoveAll(c => c.Type == @event.Data.ClaimType && c.Value == @event.Data.ClaimValue);
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }
}
