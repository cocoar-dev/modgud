using Cocoar.Auth.Domain.Events;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Marten.Events.Projections;

namespace Cocoar.Auth.Infrastructure.Persistence.Projections;

// ═══════════════════════════════════════════════════════════════════════════
// INLINE STATE PROJECTION: NORMALIZED USER STATE
// ═══════════════════════════════════════════════════════════════════════════
// Naming Convention: *State = Inline projection, single source of truth
// Use for: validation, uniqueness checks, authentication, Identity stores
// DO NOT use for: API responses, UI display (use async projections instead)
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Normalized state model for user data, projected from the event stream.
/// This provides fast query access to user information for validation and Identity.
/// </summary>
public class UserState
{
    /// <summary>
    /// The unique identifier for this user.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The unique username for this user.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// The normalized (uppercase) username for lookups.
    /// </summary>
    public string NormalizedUserName { get; set; } = string.Empty;

    /// <summary>
    /// The user's email address.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// The normalized (uppercase) email for lookups.
    /// </summary>
    public string? NormalizedEmail { get; set; }

    /// <summary>
    /// Whether the email has been confirmed.
    /// </summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>
    /// The user's phone number.
    /// </summary>
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Whether the phone number has been confirmed.
    /// </summary>
    public bool PhoneNumberConfirmed { get; set; }

    /// <summary>
    /// Whether two-factor authentication is enabled.
    /// </summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// When the lockout ends (null if not locked out).
    /// </summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>
    /// Whether lockout is enabled for this user.
    /// </summary>
    public bool LockoutEnabled { get; set; }

    /// <summary>
    /// The number of failed access attempts.
    /// </summary>
    public int AccessFailedCount { get; set; }

    /// <summary>
    /// The user's first name.
    /// </summary>
    public string? FirstName { get; set; }

    /// <summary>
    /// The user's last name.
    /// </summary>
    public string? LastName { get; set; }

    /// <summary>
    /// Whether this user is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this user has been deleted (soft delete).
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The roles assigned to this user (role IDs only - normalized).
    /// </summary>
    public List<Guid> Roles { get; set; } = [];

    /// <summary>
    /// The claims assigned to this user.
    /// </summary>
    public List<ClaimData> Claims { get; set; } = [];

    /// <summary>
    /// When this user was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this user was last modified.
    /// </summary>
    public DateTimeOffset? ModifiedAt { get; set; }
}

/// <summary>
/// Value object for a user claim (not a projection - just embedded data).
/// </summary>
public record ClaimData(string Type, string Value);

/// <summary>
/// Inline event-based projection that maintains <see cref="UserState"/> documents from user events.
/// Runs synchronously with writes for immediate consistency.
/// </summary>
public class UserStateProjection : EventProjection
{
    /// <summary>
    /// Create a new state model when a user is created.
    /// </summary>
    public UserState Create(IEvent<UserCreated> @event)
    {
        var data = @event.Data;
        return new UserState
        {
            Id = @event.StreamId,
            UserName = data.UserName,
            NormalizedUserName = data.UserName.ToUpperInvariant(),
            Email = data.Email,
            NormalizedEmail = data.Email?.ToUpperInvariant(),
            PhoneNumber = data.PhoneNumber,
            FirstName = data.FirstName,
            LastName = data.LastName,
            IsActive = data.IsActive,
            LockoutEnabled = data.LockoutEnabled,
            Roles = data.Roles.ToList(),
            CreatedAt = @event.Timestamp
        };
    }

    public void Project(IEvent<UserNameChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.UserName = @event.Data.NewUserName;
        model.NormalizedUserName = @event.Data.NewUserName.ToUpperInvariant();
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserEmailChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.Email = @event.Data.NewEmail;
        model.NormalizedEmail = @event.Data.NewEmail?.ToUpperInvariant();
        model.EmailConfirmed = false;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserPhoneNumberChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.PhoneNumber = @event.Data.NewPhoneNumber;
        model.PhoneNumberConfirmed = false;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserProfileNameChanged> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.FirstName = @event.Data.NewFirstName;
        model.LastName = @event.Data.NewLastName;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserActivated> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.IsActive = true;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserDeactivated> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.IsActive = false;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserDeleted> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.IsDeleted = true;
        model.IsActive = false;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserRoleAssigned> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        if (!model.Roles.Contains(@event.Data.RoleId))
        {
            model.Roles.Add(@event.Data.RoleId);
        }
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserRoleRemoved> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.Roles.Remove(@event.Data.RoleId);
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserClaimAdded> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        var claim = new ClaimData(@event.Data.ClaimType, @event.Data.ClaimValue);
        if (!model.Claims.Any(c => c.Type == claim.Type && c.Value == claim.Value))
        {
            model.Claims.Add(claim);
        }
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserClaimRemoved> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        var claim = model.Claims.FirstOrDefault(c => c.Type == @event.Data.ClaimType && c.Value == @event.Data.ClaimValue);
        if (claim is not null)
        {
            model.Claims.Remove(claim);
        }
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserTwoFactorEnabled> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.TwoFactorEnabled = true;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserTwoFactorDisabled> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.TwoFactorEnabled = false;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserLockedOut> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.LockoutEnd = @event.Data.LockoutEnd;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserUnlocked> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.LockoutEnd = null;
        model.AccessFailedCount = 0;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserEmailConfirmed> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.EmailConfirmed = true;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserPhoneNumberConfirmed> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.PhoneNumberConfirmed = true;
        model.ModifiedAt = DateTimeOffset.UtcNow;
        ops.Store(model);
    }

    public void Project(IEvent<UserLoginFailed> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.AccessFailedCount++;
        ops.Store(model);
    }

    public void Project(IEvent<UserLoggedIn> @event, IDocumentOperations ops)
    {
        var model = ops.LoadAsync<UserState>(@event.Data.UserId).GetAwaiter().GetResult();
        if (model is null) return;

        model.AccessFailedCount = 0;
        ops.Store(model);
    }
}
