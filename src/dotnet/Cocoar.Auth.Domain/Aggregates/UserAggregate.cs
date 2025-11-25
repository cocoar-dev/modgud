using Cocoar.Auth.Domain.Events;

namespace Cocoar.Auth.Domain.Aggregates;

/// <summary>
/// Event-sourced aggregate for user profile data.
/// Contains all auditable user information.
/// Security-sensitive data (passwords, stamps) is stored separately in UserSecurityData.
/// </summary>
public class UserAggregate
{
    /// <summary>
    /// The unique identifier for this user.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    /// The unique username for this user.
    /// </summary>
    public string UserName { get; private set; } = string.Empty;

    /// <summary>
    /// The normalized (uppercase) username for lookups.
    /// </summary>
    public string NormalizedUserName { get; private set; } = string.Empty;

    /// <summary>
    /// The user's email address.
    /// </summary>
    public string? Email { get; private set; }

    /// <summary>
    /// The normalized (uppercase) email for lookups.
    /// </summary>
    public string? NormalizedEmail { get; private set; }

    /// <summary>
    /// Whether the email has been confirmed.
    /// </summary>
    public bool EmailConfirmed { get; private set; }

    /// <summary>
    /// The user's phone number.
    /// </summary>
    public string? PhoneNumber { get; private set; }

    /// <summary>
    /// Whether the phone number has been confirmed.
    /// </summary>
    public bool PhoneNumberConfirmed { get; private set; }

    /// <summary>
    /// Whether two-factor authentication is enabled.
    /// </summary>
    public bool TwoFactorEnabled { get; private set; }

    /// <summary>
    /// When the lockout ends (null if not locked out).
    /// </summary>
    public DateTimeOffset? LockoutEnd { get; private set; }

    /// <summary>
    /// Whether lockout is enabled for this user.
    /// </summary>
    public bool LockoutEnabled { get; private set; }

    /// <summary>
    /// The number of failed access attempts.
    /// </summary>
    public int AccessFailedCount { get; private set; }

    /// <summary>
    /// The user's first name.
    /// </summary>
    public string? FirstName { get; private set; }

    /// <summary>
    /// The user's last name.
    /// </summary>
    public string? LastName { get; private set; }

    /// <summary>
    /// Whether this user is active.
    /// </summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Whether this user has been deleted (soft delete).
    /// </summary>
    public bool IsDeleted { get; private set; }

    /// <summary>
    /// The roles assigned to this user (role IDs).
    /// </summary>
    public List<Guid> Roles { get; private set; } = [];

    /// <summary>
    /// The claims assigned to this user.
    /// </summary>
    public List<UserClaimData> Claims { get; private set; } = [];

    /// <summary>
    /// When this user was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// When this user was last modified.
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

    public void Apply(UserCreated @event)
    {
        Id = @event.UserId;
        UserName = @event.UserName;
        NormalizedUserName = @event.UserName.ToUpperInvariant();
        Email = @event.Email;
        NormalizedEmail = @event.Email?.ToUpperInvariant();
        PhoneNumber = @event.PhoneNumber;
        FirstName = @event.FirstName;
        LastName = @event.LastName;
        IsActive = @event.IsActive;
        LockoutEnabled = @event.LockoutEnabled;
        Roles = @event.Roles.ToList();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserNameChanged @event)
    {
        UserName = @event.NewUserName;
        NormalizedUserName = @event.NewUserName.ToUpperInvariant();
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserEmailChanged @event)
    {
        Email = @event.NewEmail;
        NormalizedEmail = @event.NewEmail?.ToUpperInvariant();
        EmailConfirmed = false; // Reset confirmation when email changes
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserPhoneNumberChanged @event)
    {
        PhoneNumber = @event.NewPhoneNumber;
        PhoneNumberConfirmed = false; // Reset confirmation when phone changes
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserProfileNameChanged @event)
    {
        FirstName = @event.NewFirstName;
        LastName = @event.NewLastName;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserActivated @event)
    {
        IsActive = true;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserDeactivated @event)
    {
        IsActive = false;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserDeleted @event)
    {
        IsDeleted = true;
        IsActive = false;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserRoleAssigned @event)
    {
        if (!Roles.Contains(@event.RoleId))
        {
            Roles.Add(@event.RoleId);
        }
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserRoleRemoved @event)
    {
        Roles.Remove(@event.RoleId);
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserClaimAdded @event)
    {
        var claim = new UserClaimData(@event.ClaimType, @event.ClaimValue);
        if (!Claims.Any(c => c.Type == claim.Type && c.Value == claim.Value))
        {
            Claims.Add(claim);
        }
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserClaimRemoved @event)
    {
        var claim = Claims.FirstOrDefault(c => c.Type == @event.ClaimType && c.Value == @event.ClaimValue);
        if (claim is not null)
        {
            Claims.Remove(claim);
        }
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserTwoFactorEnabled @event)
    {
        TwoFactorEnabled = true;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserTwoFactorDisabled @event)
    {
        TwoFactorEnabled = false;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserLockedOut @event)
    {
        LockoutEnd = @event.LockoutEnd;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserUnlocked @event)
    {
        LockoutEnd = null;
        AccessFailedCount = 0;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserEmailConfirmed @event)
    {
        EmailConfirmed = true;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserPhoneNumberConfirmed @event)
    {
        PhoneNumberConfirmed = true;
        ModifiedAt = DateTimeOffset.UtcNow;
    }

    public void Apply(UserLoginFailed @event)
    {
        AccessFailedCount++;
    }

    public void Apply(UserLoggedIn @event)
    {
        AccessFailedCount = 0; // Reset on successful login
    }

    // These events don't change aggregate state, but are recorded for audit
    public void Apply(UserPasswordChanged @event) { }
    public void Apply(UserRecoveryCodesRegenerated @event) { }
    public void Apply(UserSessionsInvalidated @event) { }
}

/// <summary>
/// Represents a claim (type/value pair) for a user.
/// </summary>
public record UserClaimData(string Type, string Value);
