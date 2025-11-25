namespace Cocoar.Auth.Application.Models;

/// <summary>
/// Denormalized read model for user details with embedded role information.
/// This model is for display/API purposes only - no security-sensitive data.
/// Updated asynchronously via the Async Daemon (eventually consistent).
/// </summary>
public class UserDetailsReadModel
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
    /// The user's email address.
    /// </summary>
    public string? Email { get; set; }

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
    /// Whether lockout is enabled for this user.
    /// </summary>
    public bool LockoutEnabled { get; set; }

    /// <summary>
    /// When the lockout ends (null if not locked out).
    /// </summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>
    /// Number of failed login attempts.
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
    /// The user's full name (computed).
    /// </summary>
    public string FullName => string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName)
        ? UserName
        : $"{FirstName} {LastName}".Trim();

    /// <summary>
    /// Whether this user is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this user has been deleted (soft delete).
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// The roles assigned to this user (denormalized with full role info).
    /// </summary>
    public List<RoleInfo> Roles { get; set; } = [];

    /// <summary>
    /// The claims assigned to this user.
    /// </summary>
    public List<ClaimInfo> Claims { get; set; } = [];

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
/// Embedded role information for denormalized user view.
/// </summary>
public class RoleInfo
{
    /// <summary>
    /// The role's unique identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The role's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The role's description.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Embedded claim information for denormalized user view.
/// </summary>
public record ClaimInfo(string Type, string Value);
