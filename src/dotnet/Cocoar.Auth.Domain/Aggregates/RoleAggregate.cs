using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Event-sourced aggregate for role data.
/// Contains all auditable role information.
/// </summary>
public class RoleAggregate
{
    /// <summary>
    /// The unique identifier for this role.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// The name of the role.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// The normalized (uppercase) name for lookups.
    /// </summary>
    public string NormalizedName { get; private set; } = string.Empty;

    /// <summary>
    /// A description of the role.
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// A human-friendly display name for the role.
    /// </summary>
    public string? DisplayName { get; private set; }

    /// <summary>
    /// An email address associated with the role.
    /// </summary>
    public string? Email { get; private set; }

    /// <summary>
    /// The ID of the API resource this role is bound to, if any.
    /// </summary>
    public Guid? BoundToApiResourceId { get; private set; }

    /// <summary>
    /// Whether this role has been deleted (soft delete).
    /// </summary>
    public bool IsDeleted { get; private set; }

    /// <summary>
    /// The claims assigned to this role.
    /// </summary>
    public List<(string Type, string Value)> Claims { get; private set; } = [];

    /// <summary>
    /// When this role was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When this role was last modified.
    /// </summary>
    public DateTimeOffset? ModifiedAt { get; private set; }

    /// <summary>
    /// The current version of the aggregate (event stream version).
    /// </summary>
    public int Version { get; private set; }

    // ═══════════════════════════════════════════════════════════════════════
    // EVENT APPLICATION METHODS
    // These methods are called by Marten when replaying events to build state.
    // ═══════════════════════════════════════════════════════════════════════

    public void Apply(RoleCreated @event)
    {
        Id = @event.RoleId;
        Name = @event.Name;
        NormalizedName = @event.Name.ToUpperInvariant();
        Description = @event.Description;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleNameChanged @event)
    {
        Name = @event.NewName;
        NormalizedName = @event.NewName.ToUpperInvariant();
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleDescriptionChanged @event)
    {
        Description = @event.NewDescription;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleDeleted @event)
    {
        IsDeleted = true;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleClaimAdded @event)
    {
        var claim = (Type: @event.ClaimType, Value: @event.ClaimValue);
        if (!Claims.Contains(claim))
        {
            Claims.Add(claim);
        }
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleClaimRemoved @event)
    {
        Claims.Remove((Type: @event.ClaimType, Value: @event.ClaimValue));
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleDisplayNameChanged @event)
    {
        DisplayName = @event.NewDisplayName;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleEmailChanged @event)
    {
        Email = @event.NewEmail;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(RoleBoundToApiResourceChanged @event)
    {
        BoundToApiResourceId = @event.NewApiResourceId;
        ModifiedAt = DateTimeOffset.UtcNow;
    }
}
