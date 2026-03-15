using System.Text.Json.Serialization;
using Cocoar.Auth.Domain.Common;

namespace Cocoar.Auth.Domain.Entities;

/// <summary>
/// Represents a user in the identity system.
/// </summary>
public class ApplicationUser : Entity
{
    /// <summary>
    /// The unique username for this user.
    /// </summary>
    [JsonInclude]
    public string UserName { get; private set; } = string.Empty;

    /// <summary>
    /// The normalized (uppercase) username for lookups.
    /// </summary>
    [JsonInclude]
    public string NormalizedUserName { get; private set; } = string.Empty;

    /// <summary>
    /// The user's email address.
    /// </summary>
    [JsonInclude]
    public string? Email { get; private set; }

    /// <summary>
    /// The normalized (uppercase) email for lookups.
    /// </summary>
    [JsonInclude]
    public string? NormalizedEmail { get; private set; }

    /// <summary>
    /// Whether the email has been confirmed.
    /// </summary>
    [JsonInclude]
    public bool EmailConfirmed { get; private set; }

    /// <summary>
    /// The hashed password.
    /// </summary>
    [JsonInclude]
    public string? PasswordHash { get; private set; }

    /// <summary>
    /// A random value that changes when security-sensitive data changes.
    /// </summary>
    [JsonInclude]
    public string? SecurityStamp { get; private set; }

    /// <summary>
    /// A random value that changes when the user is persisted.
    /// </summary>
    [JsonInclude]
    public string? ConcurrencyStamp { get; private set; }

    /// <summary>
    /// The user's phone number.
    /// </summary>
    [JsonInclude]
    public string? PhoneNumber { get; private set; }

    /// <summary>
    /// Whether the phone number has been confirmed.
    /// </summary>
    [JsonInclude]
    public bool PhoneNumberConfirmed { get; private set; }

    /// <summary>
    /// Whether two-factor authentication is enabled.
    /// </summary>
    [JsonInclude]
    public bool TwoFactorEnabled { get; private set; }

    /// <summary>
    /// When the lockout ends (null if not locked out).
    /// </summary>
    [JsonInclude]
    public DateTimeOffset? LockoutEnd { get; private set; }

    /// <summary>
    /// Whether lockout is enabled for this user.
    /// </summary>
    [JsonInclude]
    public bool LockoutEnabled { get; private set; }

    /// <summary>
    /// The number of failed access attempts.
    /// </summary>
    [JsonInclude]
    public int AccessFailedCount { get; private set; }

    /// <summary>
    /// The user's first name.
    /// </summary>
    [JsonInclude]
    public string? FirstName { get; private set; }

    /// <summary>
    /// The user's last name.
    /// </summary>
    [JsonInclude]
    public string? LastName { get; private set; }

    /// <summary>
    /// When this user account expires (null means no expiration).
    /// </summary>
    [JsonInclude]
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>
    /// Whether this user is active.
    /// </summary>
    [JsonInclude]
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Whether this user has been deleted (soft delete).
    /// </summary>
    [JsonInclude]
    public bool IsDeleted { get; private set; }

    /// <summary>
    /// Whether this user's personal data has been erased (GDPR).
    /// </summary>
    [JsonInclude]
    public bool IsDataErased { get; private set; }

    /// <summary>
    /// The roles assigned to this user (role IDs).
    /// </summary>
    [JsonInclude]
    public List<Guid> Roles { get; private set; } = [];

    /// <summary>
    /// User claims.
    /// </summary>
    [JsonInclude]
    public List<UserClaim> Claims { get; private set; } = [];

    /// <summary>
    /// User login providers.
    /// </summary>
    [JsonInclude]
    public List<UserLogin> Logins { get; private set; } = [];

    /// <summary>
    /// User tokens.
    /// </summary>
    [JsonInclude]
    public List<UserToken> Tokens { get; private set; } = [];

    // For Marten deserialization
    private ApplicationUser() : base() { }

    public ApplicationUser(string userName, string? email = null) : base()
    {
        SetUserName(userName);
        SetEmail(email);
        SecurityStamp = Guid.NewGuid().ToString();
        ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    public void SetUserName(string userName)
    {
        UserName = userName;
        NormalizedUserName = userName.ToUpperInvariant();
        MarkModified();
    }

    public void SetEmail(string? email)
    {
        Email = email;
        NormalizedEmail = email?.ToUpperInvariant();
        MarkModified();
    }

    public void SetEmailConfirmed(bool confirmed)
    {
        EmailConfirmed = confirmed;
        MarkModified();
    }

    public void SetPasswordHash(string? passwordHash)
    {
        PasswordHash = passwordHash;
        MarkModified();
    }

    public void SetSecurityStamp(string? securityStamp)
    {
        SecurityStamp = securityStamp;
        MarkModified();
    }

    public void SetConcurrencyStamp(string? concurrencyStamp)
    {
        ConcurrencyStamp = concurrencyStamp;
    }

    public void SetPhoneNumber(string? phoneNumber)
    {
        PhoneNumber = phoneNumber;
        MarkModified();
    }

    public void SetPhoneNumberConfirmed(bool confirmed)
    {
        PhoneNumberConfirmed = confirmed;
        MarkModified();
    }

    public void SetTwoFactorEnabled(bool enabled)
    {
        TwoFactorEnabled = enabled;
        MarkModified();
    }

    public void SetLockoutEnd(DateTimeOffset? lockoutEnd)
    {
        LockoutEnd = lockoutEnd;
        MarkModified();
    }

    public void SetLockoutEnabled(bool enabled)
    {
        LockoutEnabled = enabled;
        MarkModified();
    }

    public void SetAccessFailedCount(int count)
    {
        AccessFailedCount = count;
        MarkModified();
    }

    public void IncrementAccessFailedCount()
    {
        AccessFailedCount++;
        MarkModified();
    }

    public void ResetAccessFailedCount()
    {
        AccessFailedCount = 0;
        MarkModified();
    }

    public void SetFirstName(string? firstName)
    {
        FirstName = firstName;
        MarkModified();
    }

    public void SetLastName(string? lastName)
    {
        LastName = lastName;
        MarkModified();
    }

    public void SetExpiresAt(DateTimeOffset? expiresAt)
    {
        ExpiresAt = expiresAt;
        MarkModified();
    }

    public void SetIsActive(bool isActive)
    {
        IsActive = isActive;
        MarkModified();
    }

    public void AddRole(Guid roleId)
    {
        if (!Roles.Contains(roleId))
        {
            Roles.Add(roleId);
            MarkModified();
        }
    }

    public void RemoveRole(Guid roleId)
    {
        if (Roles.Remove(roleId))
        {
            MarkModified();
        }
    }

    public void AddClaim(string type, string value)
    {
        Claims.Add(new UserClaim(type, value));
        MarkModified();
    }

    public void RemoveClaim(string type, string value)
    {
        var claim = Claims.FirstOrDefault(c => c.Type == type && c.Value == value);
        if (claim is not null)
        {
            Claims.Remove(claim);
            MarkModified();
        }
    }

    public void ReplaceClaim(string type, string oldValue, string newValue)
    {
        var claim = Claims.FirstOrDefault(c => c.Type == type && c.Value == oldValue);
        if (claim is not null)
        {
            Claims.Remove(claim);
            Claims.Add(new UserClaim(type, newValue));
            MarkModified();
        }
    }

    public void AddLogin(string loginProvider, string providerKey, string? providerDisplayName)
    {
        Logins.Add(new UserLogin(loginProvider, providerKey, providerDisplayName));
        MarkModified();
    }

    public void RemoveLogin(string loginProvider, string providerKey)
    {
        var login = Logins.FirstOrDefault(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey);
        if (login is not null)
        {
            Logins.Remove(login);
            MarkModified();
        }
    }

    public void SetToken(string loginProvider, string name, string? value)
    {
        var token = Tokens.FirstOrDefault(t => t.LoginProvider == loginProvider && t.Name == name);
        if (token is not null)
        {
            Tokens.Remove(token);
        }
        if (value is not null)
        {
            Tokens.Add(new UserToken(loginProvider, name, value));
        }
        MarkModified();
    }

    public string? GetToken(string loginProvider, string name)
    {
        return Tokens.FirstOrDefault(t => t.LoginProvider == loginProvider && t.Name == name)?.Value;
    }

    public void RemoveToken(string loginProvider, string name)
    {
        var token = Tokens.FirstOrDefault(t => t.LoginProvider == loginProvider && t.Name == name);
        if (token is not null)
        {
            Tokens.Remove(token);
            MarkModified();
        }
    }

    /// <summary>
    /// Marks the user as deleted (soft delete).
    /// </summary>
    public void MarkAsDeleted()
    {
        IsDeleted = true;
        IsActive = false;
        MarkModified();
    }

    /// <summary>
    /// Restores a soft-deleted user.
    /// </summary>
    public void Restore()
    {
        IsDeleted = false;
        IsActive = true;
        MarkModified();
    }

    /// <summary>
    /// Clears all personal data from this user (GDPR erasure).
    /// The user document remains but PII is removed.
    /// </summary>
    public void ClearPersonalData()
    {
        UserName = "[DELETED]";
        NormalizedUserName = "[DELETED]";
        Email = null;
        NormalizedEmail = null;
        PhoneNumber = null;
        FirstName = null;
        LastName = null;
        PasswordHash = null;
        SecurityStamp = Guid.NewGuid().ToString(); // Invalidate all tokens
        TwoFactorEnabled = false;
        Claims.Clear();
        Tokens.Clear();
        Logins.Clear();
        IsDataErased = true;
        MarkModified();
    }
}

/// <summary>
/// Represents a claim for a user.
/// </summary>
public record UserClaim(string Type, string Value);

/// <summary>
/// Represents an external login for a user.
/// </summary>
public record UserLogin(string LoginProvider, string ProviderKey, string? ProviderDisplayName);

/// <summary>
/// Represents a token for a user.
/// </summary>
public record UserToken(string LoginProvider, string Name, string? Value);
