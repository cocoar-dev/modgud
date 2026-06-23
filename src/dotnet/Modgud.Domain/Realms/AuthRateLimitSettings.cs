namespace Modgud.Domain.Realms;

/// <summary>
/// The set of per-IP auth rate-limit policies whose ceiling a realm admin can
/// raise/lower. These mirror the hardcoded ASP.NET limiter policies of the same
/// name registered in <c>Program.cs</c>; <see cref="AuthRateLimitSettings"/> lets
/// a realm override the limit/window per policy, with the code defaults
/// (<see cref="AuthRateLimitDefaults"/>) as the baseline.
/// </summary>
public enum AuthRateLimitPolicy
{
    /// <summary>Native passwordless OTP request + native register (5/h).</summary>
    NativeOtp,
    /// <summary>Magic-link request (5/h).</summary>
    MagicLink,
    /// <summary>Forgot-password / password-reset request (5/h).</summary>
    PasswordReset,
    /// <summary>Email-OTP login (30/min).</summary>
    EmailOtp,
    /// <summary>Email verification resend (5/h).</summary>
    EmailVerification,
    /// <summary>Native passkey begin / enroll-begin / enroll (60/5min).</summary>
    PasskeyBegin,
    /// <summary>First-admin bootstrap (10/15min).</summary>
    Bootstrap,
}

/// <summary>
/// A single rate-limit ceiling: at most <see cref="PermitLimit"/> requests per
/// <see cref="WindowMinutes"/> from one partition (per-IP, per-realm). Whole
/// minutes are enough for every auth policy (the tightest is 1 minute).
/// </summary>
public record RateLimitRule
{
    public int PermitLimit { get; init; }
    public int WindowMinutes { get; init; }
}

/// <summary>
/// The shipped defaults for each <see cref="AuthRateLimitPolicy"/> — kept in code
/// so a realm that never touches the feature behaves exactly as before. They are
/// the single source of truth for both the live limiter (fallback when a realm has
/// no override) and the admin UI (shown as placeholders / reset target).
/// </summary>
public static class AuthRateLimitDefaults
{
    public static RateLimitRule For(AuthRateLimitPolicy policy) => policy switch
    {
        AuthRateLimitPolicy.NativeOtp => new RateLimitRule { PermitLimit = 5, WindowMinutes = 60 },
        AuthRateLimitPolicy.MagicLink => new RateLimitRule { PermitLimit = 5, WindowMinutes = 60 },
        AuthRateLimitPolicy.PasswordReset => new RateLimitRule { PermitLimit = 5, WindowMinutes = 60 },
        AuthRateLimitPolicy.EmailOtp => new RateLimitRule { PermitLimit = 30, WindowMinutes = 1 },
        AuthRateLimitPolicy.EmailVerification => new RateLimitRule { PermitLimit = 5, WindowMinutes = 60 },
        AuthRateLimitPolicy.PasskeyBegin => new RateLimitRule { PermitLimit = 60, WindowMinutes = 5 },
        AuthRateLimitPolicy.Bootstrap => new RateLimitRule { PermitLimit = 10, WindowMinutes = 15 },
        _ => new RateLimitRule { PermitLimit = 5, WindowMinutes = 60 },
    };

    /// <summary>The ASP.NET limiter policy name (matches <c>RequireRateLimiting</c>
    /// call sites and <c>AddPolicy</c> registrations in <c>Program.cs</c>).</summary>
    public static string PolicyName(AuthRateLimitPolicy policy) => policy switch
    {
        AuthRateLimitPolicy.NativeOtp => "native-otp",
        AuthRateLimitPolicy.MagicLink => "magic-link",
        AuthRateLimitPolicy.PasswordReset => "password-reset",
        AuthRateLimitPolicy.EmailOtp => "email-otp",
        AuthRateLimitPolicy.EmailVerification => "email-verification",
        AuthRateLimitPolicy.PasskeyBegin => "passkey-begin",
        AuthRateLimitPolicy.Bootstrap => "bootstrap",
        _ => "native-otp",
    };
}

/// <summary>
/// Per-realm overrides for the per-IP auth rate-limit ceilings. Lives as a
/// nullable sub-document on the tenant-DB <see cref="RealmSettings.RealmSettings"/>
/// aggregate (null = the realm has never touched the feature → every policy uses
/// its <see cref="AuthRateLimitDefaults"/>). A null rule for an individual policy
/// likewise falls back to that policy's default, so the doc only ever stores the
/// limits an admin actually changed.
///
/// <para>Owned by the realm-admin (not Control-Plane). The default values keep the
/// secure production posture; the knob exists so test realms, dev, or legitimately
/// bursty consumers can raise a ceiling without a modgud code change + redeploy.</para>
/// </summary>
public record AuthRateLimitSettings
{
    public RateLimitRule? NativeOtp { get; init; }
    public RateLimitRule? MagicLink { get; init; }
    public RateLimitRule? PasswordReset { get; init; }
    public RateLimitRule? EmailOtp { get; init; }
    public RateLimitRule? EmailVerification { get; init; }
    public RateLimitRule? PasskeyBegin { get; init; }
    public RateLimitRule? Bootstrap { get; init; }

    /// <summary>The configured override for a policy, or null if it inherits the default.</summary>
    public RateLimitRule? Get(AuthRateLimitPolicy policy) => policy switch
    {
        AuthRateLimitPolicy.NativeOtp => NativeOtp,
        AuthRateLimitPolicy.MagicLink => MagicLink,
        AuthRateLimitPolicy.PasswordReset => PasswordReset,
        AuthRateLimitPolicy.EmailOtp => EmailOtp,
        AuthRateLimitPolicy.EmailVerification => EmailVerification,
        AuthRateLimitPolicy.PasskeyBegin => PasskeyBegin,
        AuthRateLimitPolicy.Bootstrap => Bootstrap,
        _ => null,
    };

    /// <summary>The effective rule for a policy: the realm override if set, else
    /// the shipped default. Static so the live limiter can resolve from a possibly
    /// null settings section in one call.</summary>
    public static RateLimitRule Effective(AuthRateLimitSettings? settings, AuthRateLimitPolicy policy)
        => settings?.Get(policy) ?? AuthRateLimitDefaults.For(policy);
}
