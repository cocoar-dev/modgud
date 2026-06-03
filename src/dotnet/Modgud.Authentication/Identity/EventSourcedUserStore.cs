using Marten;
using Microsoft.AspNetCore.Identity;
using Modgud.Authentication.Domain;
using Modgud.Authentication.Events;
using Modgud.Domain.Users.Events;
using Modgud.Infrastructure.Persistence.Marten.Projections.Users;

namespace Modgud.Authentication.Identity;

/// <summary>
/// Marten-based implementation of IUserStore that supports event sourcing.
/// Appends domain events for auditable changes while maintaining backward compatibility.
/// Security-sensitive data is stored in UserSecurityData document (not event-sourced).
/// </summary>
public class EventSourcedUserStore(IDocumentSession session)
    : IUserStore<ApplicationUser>,
      IUserPasswordStore<ApplicationUser>,
      IUserEmailStore<ApplicationUser>,
      IUserLockoutStore<ApplicationUser>,
      IUserTwoFactorStore<ApplicationUser>,
      IUserAuthenticatorKeyStore<ApplicationUser>,
      IUserPhoneNumberStore<ApplicationUser>,
      IUserSecurityStampStore<ApplicationUser>
{
    // IUserSecurityStampStore<ApplicationUser> — drives the SecurityStampValidator
    // (SESSION-01) and the refresh-token security-stamp parity check (OAUTH-07).
    // The stamp is rotated by ASP.NET Core Identity itself on
    // password-change / role-change / external-login-removal, and the auth
    // pipeline uses these stores to read it back. We persist the value on the
    // ApplicationUser document directly (not as a separate event) — it's
    // operational state, not a business fact worth replaying.

    public Task SetSecurityStampAsync(ApplicationUser user, string stamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SecurityStamp = stamp;
        // Persist immediately so a concurrent reader sees the new stamp.
        // Identity's higher-level operations (UpdatePasswordHashAsync,
        // UpdateSecurityStampAsync, etc.) call UpdateAsync afterwards which
        // commits the rest of the user document; this Store is the lighter
        // "set field, save" path used by the validator pipeline.
        session.Store(user);
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.SecurityStamp);
    }

    // IUserStore<ApplicationUser>

    public async Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Check if event stream already exists (migration-created or test-created users)
        var streamState = await session.Events.FetchStreamStateAsync(user.Id, cancellationToken);
        if (streamState is null)
        {
            // New user — start event stream with creation events (single transaction)
            var events = new List<object>
            {
                new UserCreatedEvent(user.Id, user.Firstname, user.Lastname, user.Acronym, user.Email),
                new UserUserNameChangedEvent(user.Id, user.UserName),
            };
            if (!string.IsNullOrEmpty(user.PasswordHash))
                events.Add(new UserPasswordChangedEvent(user.Id, null));
            session.Events.StartStream<UserView>(user.Id, events);
        }

        // Store ApplicationUser document
        session.Store(user);

        // Create UserSecurityData document
        var securityData = UserSecurityData.Create(user.Id, user.PasswordHash);
        session.Store(securityData);

        await session.SaveChangesAsync(cancellationToken);

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        session.Store(user);

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is not null)
        {
            // Detect and append security-related events by comparing
            // the user's transient properties against the persisted UserSecurityData
            AppendSecurityChangeEvents(user, securityData);

            // Sync security data
            securityData.PasswordHash = user.PasswordHash;
            securityData.SecurityStamp = user.SecurityStamp ?? securityData.SecurityStamp;
            securityData.AccessFailedCount = user.AccessFailedCount;
            securityData.LockoutEnd = user.LockoutEnd;
            securityData.TwoFactorEnabled = user.TwoFactorEnabled;
            securityData.AuthenticatorKey = user.AuthenticatorKey;
            securityData.UpdateConcurrencyStamp();
            user.ConcurrencyStamp = securityData.ConcurrencyStamp;
            session.Store(securityData);
        }
        else
        {
            // Create SecurityData if it doesn't exist yet (e.g. migration-created users)
            securityData = UserSecurityData.Create(user.Id, user.PasswordHash);
            if (!string.IsNullOrEmpty(user.SecurityStamp))
                securityData.SecurityStamp = user.SecurityStamp;
            securityData.AccessFailedCount = user.AccessFailedCount;
            securityData.LockoutEnd = user.LockoutEnd;
            securityData.TwoFactorEnabled = user.TwoFactorEnabled;
            securityData.AuthenticatorKey = user.AuthenticatorKey;
            user.ConcurrencyStamp = securityData.ConcurrencyStamp;
            session.Store(securityData);
        }

        await session.SaveChangesAsync(cancellationToken);

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Append delete event for audit trail and projection rebuild
        session.Events.Append(user.Id, new UserDeletedEvent(user.Id));

        // Soft-delete ApplicationUser document
        user.IsDeleted = true;
        session.Store(user);

        // Delete SecurityData
        session.Delete<UserSecurityData>(user.Id);

        await session.SaveChangesAsync(cancellationToken);

        return IdentityResult.Success;
    }

    public async Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Guid.TryParse(userId, out var id))
            return null;

        var user = await session.LoadAsync<ApplicationUser>(id, cancellationToken);
        if (user is null or { IsDeleted: true })
            return null;

        await PopulateSecurityDataAsync(user, cancellationToken);

        return user;
    }

    public async Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName && !u.IsDeleted, cancellationToken);

        if (user is not null)
            await PopulateSecurityDataAsync(user, cancellationToken);

        return user;
    }

    public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.Id.ToString());
    }

    public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(user.UserName);
    }

    public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken)
    {
        user.UserName = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(user.NormalizedUserName);
    }

    public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedUserName = normalizedName ?? string.Empty;
        return Task.CompletedTask;
    }

    // IUserPasswordStore<ApplicationUser>

    public Task SetPasswordHashAsync(ApplicationUser user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public async Task<string?> GetPasswordHashAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        return securityData?.PasswordHash;
    }

    public async Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        return !string.IsNullOrEmpty(securityData?.PasswordHash);
    }

    // IUserEmailStore<ApplicationUser>

    public Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken cancellationToken)
    {
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.Email);
    }

    public Task<bool> GetEmailConfirmedAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.EmailConfirmed);
    }

    public Task SetEmailConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken cancellationToken)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public async Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = await session.Query<ApplicationUser>()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail && !u.IsDeleted, cancellationToken);

        if (user is not null)
            await PopulateSecurityDataAsync(user, cancellationToken);

        return user;
    }

    public Task<string?> GetNormalizedEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.NormalizedEmail);
    }

    public Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    // IUserLockoutStore<ApplicationUser>

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.LockoutEnd);
    }

    public Task SetLockoutEndDateAsync(ApplicationUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task<int> IncrementAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount++;
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<bool> GetLockoutEnabledAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.LockoutEnabled);
    }

    public Task SetLockoutEnabledAsync(ApplicationUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    // IUserTwoFactorStore<ApplicationUser>

    public Task SetTwoFactorEnabledAsync(ApplicationUser user, bool enabled, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.TwoFactorEnabled);
    }

    // IUserAuthenticatorKeyStore<ApplicationUser>

    public Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken cancellationToken)
    {
        user.AuthenticatorKey = key;
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        return Task.FromResult(user.AuthenticatorKey);
    }

    // IUserPhoneNumberStore<ApplicationUser> (required by PasswordSignInAsync for 2FA check)
    // Not used — we use TOTP authenticator, not SMS. All methods are no-op stubs.

    public Task SetPhoneNumberAsync(ApplicationUser user, string? phoneNumber, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<string?> GetPhoneNumberAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<bool> GetPhoneNumberConfirmedAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task SetPhoneNumberConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken cancellationToken)
        => Task.CompletedTask;

    private void AppendSecurityChangeEvents(ApplicationUser user, UserSecurityData securityData)
    {
        var events = new List<object>();

        // Lockout changed — detected by comparing user's transient properties
        // against the persisted UserSecurityData document
        if (user.LockoutEnd != securityData.LockoutEnd)
        {
            if (user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow)
            {
                events.Add(new UserLockedOutEvent(user.Id, user.LockoutEnd.Value));
            }
            else if (securityData.LockoutEnd.HasValue && !user.LockoutEnd.HasValue)
            {
                events.Add(new UserUnlockedEvent(user.Id));
            }
        }

        // A failure streak just resolved — the access-failed counter went from >0
        // back to 0 (a successful sign-in or an unlock reset it). Record it as ONE
        // aggregated audit event (Decision (b)), not one per attempt: no stream spam,
        // and an attacker spraying a victim can't inflate that victim's stream. No IP
        // (the aggregate has no single source); erasable with the user's stream.
        if (securityData.AccessFailedCount > 0 && user.AccessFailedCount == 0)
        {
            events.Add(new UserLoginFailuresObservedEvent(
                user.Id, securityData.AccessFailedCount, DateTimeOffset.UtcNow));
        }

        if (events.Count > 0)
        {
            session.Events.Append(user.Id, events.ToArray());
        }
    }

    private async Task PopulateSecurityDataAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
            return;

        user.PasswordHash = securityData.PasswordHash;
        user.SecurityStamp = securityData.SecurityStamp;
        user.ConcurrencyStamp = securityData.ConcurrencyStamp;
        user.AccessFailedCount = securityData.AccessFailedCount;
        user.LockoutEnd = securityData.LockoutEnd;
        user.TwoFactorEnabled = securityData.TwoFactorEnabled;
        user.AuthenticatorKey = securityData.AuthenticatorKey;
    }

    public void Dispose()
    {
        // IDocumentSession is managed by DI container — do not dispose here
    }
}
