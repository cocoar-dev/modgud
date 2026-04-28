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

    public IQueryable<ApplicationUser> Users => _session.Query<ApplicationUser>().Where(u => !u.IsDeleted);

    #region IUserStore

    public async Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Discard events raised during construction — UserCreated covers initial state
        user.ClearPendingEvents();

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

        // Store ApplicationUser for backward compatibility
        _session.Store(user);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Append pending domain events raised by entity mutations
        if (user.PendingEvents.Count > 0)
        {
            _session.Events.Append(user.Id, user.PendingEvents.ToArray());
            user.ClearPendingEvents();
        }

        // Sync security data — dirty tracking auto-persists loaded documents
        var securityData = await _session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is not null)
        {
            securityData.PasswordHash = user.PasswordHash;
            securityData.Logins = user.Logins.ToList();
            securityData.Tokens = user.Tokens.ToList();
            securityData.UpdateConcurrencyStamp();
        }

        // Eject + Store to handle both tracked and untracked users:
        // - Tracked (loaded via FindByIdAsync): Eject removes from identity map, Store re-adds with current state
        // - Untracked (from a different scope): Eject is a no-op, Store adds for upsert
        user.SetConcurrencyStamp(Guid.NewGuid().ToString());
        _session.Eject(user);
        _session.Store(user);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Append delete event for audit trail and projection rebuild
        _session.Events.Append(user.Id, new UserDeleted(user.Id, null));

        // Soft-delete the document
        user.MarkAsDeleted();
        _session.Eject(user);
        _session.Store(user);

        await _session.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(userId, out var id))
            return null;

        var user = await _session.LoadAsync<ApplicationUser>(id, cancellationToken);
        return user is { IsDeleted: false } ? user : null;
    }

    public async Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName && !u.IsDeleted, cancellationToken);
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
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail && !u.IsDeleted, cancellationToken);
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
        var isNew = securityData is null;
        securityData ??= UserSecurityData.Create(user.Id);

        securityData.AuthenticatorKey = key;
        securityData.UpdateConcurrencyStamp();
        if (isNew) _session.Store(securityData);
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
        var isNew = securityData is null;
        securityData ??= UserSecurityData.Create(user.Id);

        securityData.RecoveryCodes = recoveryCodes.ToList();
        securityData.UpdateConcurrencyStamp();
        if (isNew) _session.Store(securityData);

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
        // Dirty tracking persists the loaded SecurityData
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
