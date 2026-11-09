using Marten;
using Marten.Patching;
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

    private bool _deferCreateCommit;

    /// <summary>
    /// AREG-05 — while the returned scope is open, <see cref="CreateAsync"/> stages the user
    /// (event stream, <see cref="ApplicationUser"/>, <see cref="UserSecurityData"/>) on the
    /// session without committing, so a caller can add its own writes and commit everything in
    /// one <c>SaveChangesAsync</c>. <see cref="UserManager{TUser}.CreateAsync(TUser)"/> still
    /// runs its validators and sets the stamps and normalized names.
    /// </summary>
    public IDisposable DeferCreateCommit()
    {
        _deferCreateCommit = true;
        return new DeferScope(this);
    }

    private sealed class DeferScope(EventSourcedUserStore store) : IDisposable
    {
        public void Dispose() => store._deferCreateCommit = false;
    }

    public Task SetSecurityStampAsync(ApplicationUser user, string stamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        user.SecurityStamp = stamp;
        // Identity's operations that rotate the stamp (UpdatePasswordHashAsync,
        // UpdateSecurityStampAsync, ...) always call UpdateAsync afterwards, which
        // writes the new stamp to UserSecurityData. AID-01 — no whole-document Store
        // here: it would write a possibly stale user object back as a whole.
        return Task.CompletedTask;
    }

    public async Task<string?> GetSecurityStampAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // Re-fetch the AUTHORITATIVE stamp from UserSecurityData (mirrors
        // GetPasswordHashAsync). The ApplicationUser.SecurityStamp field is only a
        // transient mirror, hydrated by the UserManager finders. Returning it here
        // would mint cookies/principals with a stamp that diverges from the value
        // the SecurityStampValidator re-loads on its next pass — a silent logout
        // for every sign-in path that loaded the user raw (magic-link, passkey) or
        // built the principal by hand (OIDC/SAML). Reading the source of truth
        // makes all of those paths correct regardless of how the user was loaded.
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        return securityData?.SecurityStamp ?? user.SecurityStamp;
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

        // Create UserSecurityData document. Align its stamp with the mirror that
        // ASP.NET Identity already generated on the ApplicationUser
        // (UserManager.CreateAsync runs UpdateSecurityStampInternal before calling
        // this store). Without the alignment the two are independent GUIDs, so a
        // cookie minted from a raw-loaded user carries a stamp that never matches
        // the authoritative one and is rejected on the first validation pass.
        var securityData = UserSecurityData.Create(user.Id, user.PasswordHash);
        if (!string.IsNullOrEmpty(user.SecurityStamp))
            securityData.SecurityStamp = user.SecurityStamp;
        session.Store(securityData);

        if (!_deferCreateCommit)
            await session.SaveChangesAsync(cancellationToken);
        Remember(user);

        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);

        // AID-01 — write only what changed since this store handed the user out.
        // Writing the whole object let an update that had loaded the user before a
        // deactivation, password change or factor change put the earlier state back
        // (IsActive, PasswordHash, SecurityStamp, 2FA). Fields owned by other writers
        // stay as they are now in the database. A user this store did not load
        // (raw session load) keeps the former whole-object write.
        var changed = _loaded.TryGetValue(user, out var loaded) ? ChangedSince(loaded, user) : null;
        var newConcurrencyStamp = Guid.NewGuid().ToString();
        user.ConcurrencyStamp = newConcurrencyStamp;
        if (changed is not null)
        {
            foreach (var name in changed)
                session.Patch<ApplicationUser>(user.Id).Set<object?>(name, DocumentProperties[name].GetValue(user));
            session.Patch<ApplicationUser>(user.Id).Set(x => x.ConcurrencyStamp, newConcurrencyStamp);
        }
        else
        {
            session.Store(user);
        }
        bool Writes(string field) => changed is null || changed.Contains(field);

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is not null)
        {
            // P0-4 — patch ONLY the fields this round-trip actually owns instead of
            // storing the whole document. ASP.NET Identity calls UpdateAsync after
            // EVERY AccessFailedAsync, so a full Store here rewrote the lockout
            // fields from a load-time snapshot and silently undid whatever a
            // concurrent request had just incremented: the five-attempt lockout was
            // bypassable by firing attempts in parallel. AccessFailedCount and
            // LockoutEnd are now written exclusively by the IUserLockoutStore
            // methods below, via atomic jsonb patches, and are deliberately absent
            // from this write set. The same reasoning protects the grace-period
            // fields (SecureSetupDueAt, GracePeriodDaysOverride, TwoFactorExempt),
            // which are owned by their own admin/2FA paths.
            if (Writes(nameof(ApplicationUser.PasswordHash)))
                session.Patch<UserSecurityData>(user.Id)
                    .Set(x => x.PasswordHash, user.PasswordHash);
            if (Writes(nameof(ApplicationUser.SecurityStamp)))
                session.Patch<UserSecurityData>(user.Id)
                    .Set(x => x.SecurityStamp, user.SecurityStamp ?? securityData.SecurityStamp);
            if (Writes(nameof(ApplicationUser.TwoFactorEnabled)))
                session.Patch<UserSecurityData>(user.Id)
                    .Set(x => x.TwoFactorEnabled, user.TwoFactorEnabled);
            if (Writes(nameof(ApplicationUser.AuthenticatorKey)))
                session.Patch<UserSecurityData>(user.Id)
                    .Set(x => x.AuthenticatorKey, user.AuthenticatorKey);
            session.Patch<UserSecurityData>(user.Id)
                .Set(x => x.ConcurrencyStamp, newConcurrencyStamp);
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
            securityData.ConcurrencyStamp = newConcurrencyStamp;
            session.Store(securityData);
        }

        await session.SaveChangesAsync(cancellationToken);
        Remember(user);

        return IdentityResult.Success;
    }

    // ── AID-01 — load snapshots ──────────────────────────────────────────

    private static readonly Dictionary<string, System.Reflection.PropertyInfo> DocumentProperties =
        typeof(ApplicationUser).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>
    /// Each user object as this store handed it out (finders, create) or last wrote it. Keyed by
    /// the object, not the id: Identity's validators load the same user again (user name / e-mail
    /// uniqueness) during an update, and that fresh copy must not replace the caller's baseline.
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ApplicationUser, System.Text.Json.Nodes.JsonObject> _loaded = new();

    private System.Text.Json.Nodes.JsonObject Snapshot(ApplicationUser user)
        => System.Text.Json.Nodes.JsonNode.Parse(session.DocumentStore.Options.Serializer().ToJson(user))!.AsObject();

    private void Remember(ApplicationUser user) => _loaded.AddOrUpdate(user, Snapshot(user));

    /// <summary>
    /// The document properties whose stored form differs from the snapshot, or null when one
    /// cannot be mapped back to a property (then the caller falls back to a whole write).
    /// </summary>
    private HashSet<string>? ChangedSince(System.Text.Json.Nodes.JsonObject loaded, ApplicationUser user)
    {
        var current = Snapshot(user);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in loaded.Select(kv => kv.Key).Union(current.Select(kv => kv.Key)))
        {
            if (name == nameof(ApplicationUser.ConcurrencyStamp)) continue;
            if (System.Text.Json.Nodes.JsonNode.DeepEquals(loaded[name], current[name])) continue;
            if (!DocumentProperties.ContainsKey(name)) return null;
            changed.Add(name);
        }
        return changed;
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
        session.Delete<TestAccountEmailCode>(user.Id);

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

    // P0-4 — the lockout counter and the lockout window are DB-authoritative.
    // Both are read straight from UserSecurityData (the same "authoritative
    // re-fetch" idiom as GetSecurityStampAsync / GetTwoFactorEnabledAsync) and
    // written only through atomic jsonb patches, never as part of a whole-document
    // Store. The transient mirrors on ApplicationUser are kept in sync so callers
    // that inspect the user object still see the truth, but nothing depends on
    // them for the lockout decision.

    public async Task<DateTimeOffset?> GetLockoutEndDateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // This is the predicate behind UserManager.IsLockedOutAsync. Reading the
        // in-memory mirror meant a lockout set by a CONCURRENT request after this
        // user was loaded went unseen for the rest of the request.
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
            return user.LockoutEnd;

        user.LockoutEnd = securityData.LockoutEnd;
        return securityData.LockoutEnd;
    }

    public async Task SetLockoutEndDateAsync(ApplicationUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        user.LockoutEnd = lockoutEnd;

        if (securityData is null)
            return; // No document yet — UpdateAsync's create branch persists the mirror.

        // Lock/unlock audit events used to be derived inside UpdateAsync by diffing
        // the in-memory user against the loaded document. That diff is gone with the
        // whole-document Store, so the transition is detected here, at the only place
        // that actually changes the value.
        if (securityData.LockoutEnd != lockoutEnd)
        {
            if (lockoutEnd.HasValue && lockoutEnd > DateTimeOffset.UtcNow)
                session.Events.Append(user.Id, new UserLockedOutEvent(user.Id, lockoutEnd.Value));
            else if (securityData.LockoutEnd.HasValue && !lockoutEnd.HasValue)
                session.Events.Append(user.Id, new UserUnlockedEvent(user.Id));
        }

        session.Patch<UserSecurityData>(user.Id).Set(x => x.LockoutEnd, lockoutEnd);
    }

    public async Task<int> GetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
            return user.AccessFailedCount;

        user.AccessFailedCount = securityData.AccessFailedCount;
        return securityData.AccessFailedCount;
    }

    public async Task<int> IncrementAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Server-side jsonb increment — the "Audit #24" pattern already used for the
        // email-OTP attempt counter. A read-then-write increment lets N concurrent
        // failed logins all read the same value and write value+1, so only ONE
        // attempt in the burst is ever recorded and MaxFailedAccessAttempts never
        // trips. The patch lands every attempt regardless of concurrency.
        //
        // The flush is required, not incidental: UserManager compares this return
        // value against MaxFailedAccessAttempts to decide whether to lock, so we
        // must read back a value that includes our own increment.
        session.Patch<UserSecurityData>(user.Id).Increment(x => x.AccessFailedCount, 1);
        await session.SaveChangesAsync(cancellationToken);

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
        {
            // Migration-created user with no security document yet — fall back to the
            // pre-existing in-memory behaviour; UpdateAsync's create branch persists it.
            user.AccessFailedCount++;
            return user.AccessFailedCount;
        }

        // A racer may have incremented further between our commit and this read, so
        // this can be HIGHER than our own increment. That direction is fail-closed
        // (the burst locks out at least as early as it should), which is exactly the
        // bias we want on a brute-force counter.
        user.AccessFailedCount = securityData.AccessFailedCount;
        return securityData.AccessFailedCount;
    }

    public async Task ResetAccessFailedCountAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        user.AccessFailedCount = 0;

        if (securityData is null)
            return; // No document yet — UpdateAsync's create branch persists the mirror.

        // A failure streak just resolved — the counter went from >0 back to 0 (a
        // successful sign-in or an unlock). Record it as ONE aggregated audit event
        // (Decision (b)), not one per attempt: no stream spam, and an attacker
        // spraying a victim can't inflate that victim's stream. No IP (the aggregate
        // has no single source); erasable with the user's stream. This detector used
        // to live in UpdateAsync as a stale-vs-fresh diff; it now sits on the only
        // path that actually resets the counter, and reads the authoritative value
        // rather than the possibly-stale mirror.
        if (securityData.AccessFailedCount > 0)
        {
            session.Events.Append(user.Id, new UserLoginFailuresObservedEvent(
                user.Id, securityData.AccessFailedCount, DateTimeOffset.UtcNow));
        }

        session.Patch<UserSecurityData>(user.Id).Set(x => x.AccessFailedCount, 0);
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

    public async Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        // Audit #18 — re-fetch the AUTHORITATIVE flag from UserSecurityData, exactly
        // like GetSecurityStampAsync. user.TwoFactorEnabled is the transient mirror
        // (hydrated only by the finders). This is the predicate behind the 2FA
        // step-up gate, so a raw-loaded user handed to the gate must not be able to
        // skip the second factor because its mirror read false.
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        return securityData?.TwoFactorEnabled ?? user.TwoFactorEnabled;
    }

    // IUserAuthenticatorKeyStore<ApplicationUser>

    public Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken cancellationToken)
    {
        user.AuthenticatorKey = key;
        return Task.CompletedTask;
    }

    public async Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        // Audit #18 — authoritative re-fetch (sibling of GetTwoFactorEnabledAsync):
        // the TOTP secret used to validate a step-up code must come from the source
        // of truth, not the mirror, regardless of how the user was loaded.
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        return securityData?.AuthenticatorKey ?? user.AuthenticatorKey;
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

    // The former AppendSecurityChangeEvents lived here. Both of its detectors
    // (lockout transition, resolved failure streak) were stale-vs-fresh diffs that
    // only worked because UpdateAsync wrote the whole security document from the
    // in-memory user — the very pattern that made the lockout counter racy (P0-4).
    // They now sit on SetLockoutEndDateAsync / ResetAccessFailedCountAsync, the
    // only paths that actually change those values.

    private async Task PopulateSecurityDataAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var securityData = await session.LoadAsync<UserSecurityData>(user.Id, cancellationToken);
        if (securityData is null)
        {
            Remember(user);
            return;
        }

        user.PasswordHash = securityData.PasswordHash;
        user.SecurityStamp = securityData.SecurityStamp;
        user.ConcurrencyStamp = securityData.ConcurrencyStamp;
        user.AccessFailedCount = securityData.AccessFailedCount;
        user.LockoutEnd = securityData.LockoutEnd;
        user.TwoFactorEnabled = securityData.TwoFactorEnabled;
        user.AuthenticatorKey = securityData.AuthenticatorKey;
        Remember(user);
    }

    public void Dispose()
    {
        // IDocumentSession is managed by DI container — do not dispose here
    }
}
