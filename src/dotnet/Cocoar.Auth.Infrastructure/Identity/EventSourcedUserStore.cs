using System.Security.Claims;
using Cocoar.Auth.Domain.Entities;
using Cocoar.Auth.Domain.Events;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace Cocoar.Auth.Infrastructure.Identity;

/// <summary>
/// Marten-based implementation of IUserStore that supports event sourcing.
/// Appends domain events for auditable changes while maintaining backward compatibility.
/// Security-sensitive data is stored in UserSecurityData document (not event-sourced).
/// </summary>
public class EventSourcedUserStore :
    IUserStore<ApplicationUser>,
    IUserPasswordStore<ApplicationUser>,
    IUserEmailStore<ApplicationUser>,
    IUserPhoneNumberStore<ApplicationUser>,
    IUserSecurityStampStore<ApplicationUser>,
    IUserLockoutStore<ApplicationUser>,
    IUserTwoFactorStore<ApplicationUser>,
    IUserClaimStore<ApplicationUser>,
    IUserLoginStore<ApplicationUser>,
    IUserAuthenticationTokenStore<ApplicationUser>,
    IUserRoleStore<ApplicationUser>,
    IQueryableUserStore<ApplicationUser>,
    IUserAuthenticatorKeyStore<ApplicationUser>,
    IUserTwoFactorRecoveryCodeStore<ApplicationUser>
{
    private readonly IDocumentSession _session;

    public EventSourcedUserStore(IDocumentSession session)
    {
        _session = session;
    }

    public IQueryable<ApplicationUser> Users => _session.Query<ApplicationUser>();

    #region IUserStore

    public async Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Append UserCreated event
        var @event = new UserCreated(
            user.Id,
            user.UserName,
            user.Email,
            user.PhoneNumber,
            user.FirstName,
            user.LastName,
            user.IsActive,
            user.LockoutEnabled,
            user.Roles.ToList());

        _session.Events.StartStream<Domain.Aggregates.UserAggregate>(user.Id, @event);

        // Create UserSecurityData document for sensitive data
        var securityData = UserSecurityData.Create(user.Id);
        securityData.PasswordHash = user.PasswordHash;
        securityData.Logins = user.Logins.ToList();
        securityData.Tokens = user.Tokens.ToList();
        _session.Store(securityData);

        // Also store ApplicationUser for backward compatibility (during migration)
        _session.Store(user);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Load existing user to detect changes
        var existingUser = await _session.LoadAsync<ApplicationUser>(user.Id, cancellationToken);
        if (existingUser is not null)
        {
            // Append events for profile changes
            AppendProfileChangeEvents(existingUser, user);
        }

        // Update security data
        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is not null)
        {
            securityData.PasswordHash = user.PasswordHash;
            securityData.Logins = user.Logins.ToList();
            securityData.Tokens = user.Tokens.ToList();
            securityData.UpdateConcurrencyStamp();
            _session.Store(securityData);
        }

        // Update ApplicationUser for backward compatibility
        user.SetConcurrencyStamp(Guid.NewGuid().ToString());
        _session.Store(user);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    private void AppendProfileChangeEvents(ApplicationUser existing, ApplicationUser updated)
    {
        var events = new List<object>();

        // Username change
        if (existing.UserName != updated.UserName)
        {
            events.Add(new UserNameChanged(updated.Id, existing.UserName, updated.UserName));
        }

        // Email change
        if (existing.Email != updated.Email)
        {
            events.Add(new UserEmailChanged(updated.Id, existing.Email, updated.Email));
        }

        // Phone number change
        if (existing.PhoneNumber != updated.PhoneNumber)
        {
            events.Add(new UserPhoneNumberChanged(updated.Id, existing.PhoneNumber, updated.PhoneNumber));
        }

        // Name change
        if (existing.FirstName != updated.FirstName || existing.LastName != updated.LastName)
        {
            events.Add(new UserProfileNameChanged(
                updated.Id,
                existing.FirstName, existing.LastName,
                updated.FirstName, updated.LastName));
        }

        // Expiration change
        if (existing.ExpiresAt != updated.ExpiresAt)
        {
            events.Add(new UserExpirationChanged(updated.Id, existing.ExpiresAt, updated.ExpiresAt));
        }

        // Active status change
        if (existing.IsActive != updated.IsActive)
        {
            events.Add(updated.IsActive
                ? new UserActivated(updated.Id)
                : new UserDeactivated(updated.Id, null));
        }

        // Email confirmed
        if (!existing.EmailConfirmed && updated.EmailConfirmed)
        {
            events.Add(new UserEmailConfirmed(updated.Id));
        }

        // Phone confirmed
        if (!existing.PhoneNumberConfirmed && updated.PhoneNumberConfirmed)
        {
            events.Add(new UserPhoneNumberConfirmed(updated.Id));
        }

        // Two-factor change
        if (existing.TwoFactorEnabled != updated.TwoFactorEnabled)
        {
            events.Add(updated.TwoFactorEnabled
                ? new UserTwoFactorEnabled(updated.Id)
                : new UserTwoFactorDisabled(updated.Id));
        }

        // Lockout change
        if (existing.LockoutEnd != updated.LockoutEnd)
        {
            if (updated.LockoutEnd.HasValue && updated.LockoutEnd > DateTimeOffset.UtcNow)
            {
                events.Add(new UserLockedOut(updated.Id, updated.LockoutEnd, LockoutReason.TooManyFailedAttempts));
            }
            else if (existing.LockoutEnd.HasValue && !updated.LockoutEnd.HasValue)
            {
                events.Add(new UserUnlocked(updated.Id, null));
            }
        }

        // Role changes
        var addedRoles = updated.Roles.Except(existing.Roles);
        var removedRoles = existing.Roles.Except(updated.Roles);

        foreach (var roleId in addedRoles)
        {
            events.Add(new UserRoleAssigned(updated.Id, roleId));
        }

        foreach (var roleId in removedRoles)
        {
            events.Add(new UserRoleRemoved(updated.Id, roleId));
        }

        // Claim changes
        var existingClaims = existing.Claims.Select(c => (c.Type, c.Value)).ToHashSet();
        var updatedClaims = updated.Claims.Select(c => (c.Type, c.Value)).ToHashSet();
        var addedClaims = updatedClaims.Except(existingClaims);
        var removedClaims = existingClaims.Except(updatedClaims);

        foreach (var (type, value) in addedClaims)
        {
            events.Add(new UserClaimAdded(updated.Id, type, value));
        }

        foreach (var (type, value) in removedClaims)
        {
            events.Add(new UserClaimRemoved(updated.Id, type, value));
        }

        // Password change (metadata only - no hash stored in event)
        if (existing.PasswordHash != updated.PasswordHash && !string.IsNullOrEmpty(updated.PasswordHash))
        {
            events.Add(new UserPasswordChanged(updated.Id, PasswordChangeType.UserChange, null));
        }

        // External login changes
        var existingLogins = existing.Logins.Select(l => l.LoginProvider).ToHashSet();
        var updatedLogins = updated.Logins.Select(l => l.LoginProvider).ToHashSet();

        foreach (var login in updated.Logins)
        {
            if (!existingLogins.Contains(login.LoginProvider))
            {
                events.Add(new UserExternalLoginLinked(updated.Id, login.LoginProvider, login.ProviderDisplayName));
            }
        }

        foreach (var login in existing.Logins)
        {
            if (!updatedLogins.Contains(login.LoginProvider))
            {
                events.Add(new UserExternalLoginRemoved(updated.Id, login.LoginProvider));
            }
        }

        // Append all events
        if (events.Count > 0)
        {
            _session.Events.Append(updated.Id, events.ToArray());
        }
    }

    public async Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Append delete event (soft delete in event stream)
        _session.Events.Append(user.Id, new UserDeleted(user.Id, null));

        // Delete security data
        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is not null)
        {
            _session.Delete(securityData);
        }

        // Delete ApplicationUser
        _session.Delete(user);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(userId, out var id))
            return null;

        return await _session.LoadAsync<ApplicationUser>(id, cancellationToken);
    }

    public async Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);
    }

    public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.NormalizedUserName);
    }

    public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.Id.ToString());
    }

    public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.UserName);
    }

    public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.CompletedTask;
    }

    public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        if (userName is not null)
            user.SetUserName(userName);
        return Task.CompletedTask;
    }

    #endregion

    #region IUserPasswordStore

    public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetPasswordHash(passwordHash);
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.PasswordHash);
    }

    public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }

    #endregion

    #region IUserEmailStore

    public async Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);
    }

    public Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetEmail(email);
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.Email);
    }

    public Task<bool> GetEmailConfirmedAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.EmailConfirmed);
    }

    public Task SetEmailConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetEmailConfirmed(confirmed);
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.NormalizedEmail);
    }

    public Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.CompletedTask;
    }

    #endregion

    #region IUserPhoneNumberStore

    public Task SetPhoneNumberAsync(ApplicationUser user, string? phoneNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetPhoneNumber(phoneNumber);
        return Task.CompletedTask;
    }

    public Task<string?> GetPhoneNumberAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.PhoneNumber);
    }

    public Task<bool> GetPhoneNumberConfirmedAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.PhoneNumberConfirmed);
    }

    public Task SetPhoneNumberConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetPhoneNumberConfirmed(confirmed);
        return Task.CompletedTask;
    }

    #endregion

    #region IUserSecurityStampStore

    public Task SetSecurityStampAsync(ApplicationUser user, string stamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetSecurityStamp(stamp);
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.SecurityStamp);
    }

    #endregion

    #region IUserLockoutStore

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.LockoutEnd);
    }

    public Task SetLockoutEndDateAsync(ApplicationUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetLockoutEnd(lockoutEnd);
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.IncrementAccessFailedCount();
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.ResetAccessFailedCount();
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task<bool> GetLockoutEnabledAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.LockoutEnabled);
    }

    public Task SetLockoutEnabledAsync(ApplicationUser user, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetLockoutEnabled(enabled);
        return Task.CompletedTask;
    }

    #endregion

    #region IUserTwoFactorStore

    public Task SetTwoFactorEnabledAsync(ApplicationUser user, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetTwoFactorEnabled(enabled);
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.TwoFactorEnabled);
    }

    #endregion

    #region IUserClaimStore

    public Task<IList<Claim>> GetClaimsAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        var claims = user.Claims.Select(c => new Claim(c.Type, c.Value)).ToList();
        return Task.FromResult<IList<Claim>>(claims);
    }

    public Task AddClaimsAsync(ApplicationUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        foreach (var claim in claims)
        {
            user.AddClaim(claim.Type, claim.Value);
        }
        return Task.CompletedTask;
    }

    public Task ReplaceClaimAsync(ApplicationUser user, Claim claim, Claim newClaim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.ReplaceClaim(claim.Type, claim.Value, newClaim.Value);
        return Task.CompletedTask;
    }

    public Task RemoveClaimsAsync(ApplicationUser user, IEnumerable<Claim> claims, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        foreach (var claim in claims)
        {
            user.RemoveClaim(claim.Type, claim.Value);
        }
        return Task.CompletedTask;
    }

    public async Task<IList<ApplicationUser>> GetUsersForClaimAsync(Claim claim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var users = await _session.Query<ApplicationUser>()
            .Where(u => u.Claims.Any(c => c.Type == claim.Type && c.Value == claim.Value))
            .ToListAsync(cancellationToken);
        return users.ToList();
    }

    #endregion

    #region IUserLoginStore

    public Task AddLoginAsync(ApplicationUser user, UserLoginInfo login, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.AddLogin(login.LoginProvider, login.ProviderKey, login.ProviderDisplayName);
        return Task.CompletedTask;
    }

    public Task RemoveLoginAsync(ApplicationUser user, string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.RemoveLogin(loginProvider, providerKey);
        return Task.CompletedTask;
    }

    public Task<IList<UserLoginInfo>> GetLoginsAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        var logins = user.Logins.Select(l => new UserLoginInfo(l.LoginProvider, l.ProviderKey, l.ProviderDisplayName)).ToList();
        return Task.FromResult<IList<UserLoginInfo>>(logins);
    }

    public async Task<ApplicationUser?> FindByLoginAsync(string loginProvider, string providerKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.Logins.Any(l => l.LoginProvider == loginProvider && l.ProviderKey == providerKey), cancellationToken);
    }

    #endregion

    #region IUserAuthenticationTokenStore

    public Task SetTokenAsync(ApplicationUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SetToken(loginProvider, name, value);
        return Task.CompletedTask;
    }

    public Task RemoveTokenAsync(ApplicationUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.RemoveToken(loginProvider, name);
        return Task.CompletedTask;
    }

    public Task<string?> GetTokenAsync(ApplicationUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.GetToken(loginProvider, name));
    }

    #endregion

    #region IUserRoleStore

    public async Task AddToRoleAsync(ApplicationUser user, string roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roleName);

        var normalizedRoleName = roleName.ToUpperInvariant();
        var role = await _session.Query<ApplicationRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        if (role is not null)
        {
            user.AddRole(role.Id);
        }
    }

    public async Task RemoveFromRoleAsync(ApplicationUser user, string roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roleName);

        var normalizedRoleName = roleName.ToUpperInvariant();
        var role = await _session.Query<ApplicationRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        if (role is not null)
        {
            user.RemoveRole(role.Id);
        }
    }

    public async Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        if (user.Roles.Count == 0)
            return [];

        var roles = await _session.Query<ApplicationRole>()
            .Where(r => user.Roles.Contains(r.Id))
            .ToListAsync(cancellationToken);

        return roles.Select(r => r.Name!).ToList();
    }

    public async Task<bool> IsInRoleAsync(ApplicationUser user, string roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roleName);

        var normalizedRoleName = roleName.ToUpperInvariant();
        var role = await _session.Query<ApplicationRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        return role is not null && user.Roles.Contains(role.Id);
    }

    public async Task<IList<ApplicationUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(roleName);

        var normalizedRoleName = roleName.ToUpperInvariant();
        var role = await _session.Query<ApplicationRole>()
            .FirstOrDefaultAsync(r => r.NormalizedName == normalizedRoleName, cancellationToken);

        if (role is null)
            return [];

        var users = await _session.Query<ApplicationUser>()
            .Where(u => u.Roles.Contains(role.Id))
            .ToListAsync(cancellationToken);

        return users.ToList();
    }

    #endregion

    #region IUserAuthenticatorKeyStore

    public async Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
        {
            securityData = UserSecurityData.Create(user.Id);
        }

        securityData.AuthenticatorKey = key;
        securityData.UpdateConcurrencyStamp();
        _session.Store(securityData);
        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        return securityData?.AuthenticatorKey;
    }

    #endregion

    #region IUserTwoFactorRecoveryCodeStore

    public async Task ReplaceCodesAsync(ApplicationUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(recoveryCodes);

        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
        {
            securityData = UserSecurityData.Create(user.Id);
        }

        securityData.RecoveryCodes = recoveryCodes.ToList();
        securityData.UpdateConcurrencyStamp();
        _session.Store(securityData);

        // Append event for audit trail (no sensitive data)
        _session.Events.Append(user.Id, new UserRecoveryCodesRegenerated(user.Id, securityData.RecoveryCodes.Count));

        await _session.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RedeemCodeAsync(ApplicationUser user, string code, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(code);

        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
        {
            return false;
        }

        // Recovery codes are stored hashed, but Identity's default implementation stores them plain
        // We follow the same pattern here
        var matchingCode = securityData.RecoveryCodes.FirstOrDefault(c => c == code);
        if (matchingCode is null)
        {
            return false;
        }

        securityData.RecoveryCodes.Remove(matchingCode);
        securityData.UpdateConcurrencyStamp();
        _session.Store(securityData);
        await _session.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<int> CountCodesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        return securityData?.RecoveryCodes.Count ?? 0;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Appends a login event for security monitoring.
    /// </summary>
    public async Task AppendLoginEventAsync(Guid userId, string? ipAddress, string? userAgent, CancellationToken cancellationToken)
    {
        _session.Events.Append(userId, new UserLoggedIn(userId, ipAddress, userAgent));
        await _session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Appends a failed login event for security monitoring.
    /// </summary>
    public async Task AppendLoginFailedEventAsync(Guid userId, string? ipAddress, string? userAgent, LoginFailureReason reason, CancellationToken cancellationToken)
    {
        _session.Events.Append(userId, new UserLoginFailed(userId, ipAddress, userAgent, reason));
        await _session.SaveChangesAsync(cancellationToken);
    }

    #endregion

    public void Dispose()
    {
        // Marten session is managed by DI
    }
}
