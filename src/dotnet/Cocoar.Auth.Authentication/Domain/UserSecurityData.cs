namespace Cocoar.Auth.Authentication.Domain;

/// <summary>
/// Stores security-sensitive data separately from the event stream.
/// Password hashes and tokens must NEVER appear in events.
/// </summary>
public class UserSecurityData
{
    public Guid Id { get; set; }
    public string? PasswordHash { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString();
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();

    // Lockout (brute force protection)
    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }

    // MFA / TOTP
    public string? AuthenticatorKey { get; set; }
    public bool TwoFactorEnabled { get; set; }

    // 2FA grace period — when AuthenticationMinimumLevel >= 1 is enforced, users without
    // any 2FA method get a configurable number of days (AppSettings.TwoFactorGracePeriodDays)
    // to set one up. The due date is stamped on the first post-enforcement login that
    // triggers the check. Null means the user hasn't been checked yet (pre-enforcement
    // installs, or user already had 2FA so the check never fired).
    public DateTime? SecureSetupDueAt { get; set; }

    // Per-user grace override (null = use AppSettings.TwoFactorGracePeriodDays). Setting
    // a larger number for one user (e.g. 365) gives them a longer runway without loosening
    // the global policy. The value is the total days granted when stamping a fresh grace —
    // resetting grace for the user uses this number instead of the default.
    public int? GracePeriodDaysOverride { get; set; }

    // Hard opt-out: service accounts, legacy users, or emergency exceptions that truly
    // cannot carry a 2FA factor. When true, login skips the grace check entirely and the
    // enforcement middleware lets the user through as if they had 2FA configured. Admin
    // is expected to audit this flag; we log a warning on every exempt request.
    public bool TwoFactorExempt { get; set; }

    public static UserSecurityData Create(Guid userId, string? passwordHash = null)
    {
        return new UserSecurityData
        {
            Id = userId,
            PasswordHash = passwordHash,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        };
    }

    /// <summary>
    /// Rotates BOTH the security stamp (invalidates issued auth cookies and
    /// active sessions on the next ASP.NET Identity validation) AND the
    /// concurrency stamp (causes optimistic-concurrency conflicts on stale
    /// writes). Use whenever a security-relevant change happens, e.g. after
    /// a password change.
    /// </summary>
    public void RotateAllStamps()
    {
        SecurityStamp = Guid.NewGuid().ToString();
        ConcurrencyStamp = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Rotates only the concurrency stamp — the right call after a non-security
    /// profile edit (display name, etc.) where active sessions should remain
    /// valid but stale writers must conflict.
    /// </summary>
    public void UpdateConcurrencyStamp()
    {
        ConcurrencyStamp = Guid.NewGuid().ToString();
    }
}
