namespace Modgud.Domain.Realms;

/// <summary>
/// The auth rate-limit policies (one per public auth flow). ADR 0007: every policy
/// declares ceilings per <see cref="RateLimitDimension"/>; a realm (and an Application)
/// may override any of them, the shipped <see cref="AuthRateLimitDefaults"/> are the
/// baseline.
/// </summary>
public enum AuthRateLimitPolicy
{
    /// <summary>Native passwordless OTP request + native register + hosted passwordless request.</summary>
    NativeOtp,
    /// <summary>Magic-link request.</summary>
    MagicLink,
    /// <summary>Forgot-password / password-reset request.</summary>
    PasswordReset,
    /// <summary>Email-OTP code verify (login + 2FA).</summary>
    EmailOtp,
    /// <summary>Email verification resend.</summary>
    EmailVerification,
    /// <summary>Native passkey begin / enroll-begin / enroll, staffing and activation ceremonies.</summary>
    PasskeyBegin,
    /// <summary>First-admin bootstrap / installation (realm-independent).</summary>
    Bootstrap,
    /// <summary>The OAuth token endpoint.</summary>
    OAuthToken,
    /// <summary>Web self-registration form submit.</summary>
    SelfRegistration,
    /// <summary>ADR 0008 — interactive password login: failures per trusted device
    /// (<see cref="RateLimitDimension.Device"/>), per user from untrusted clients
    /// (<see cref="RateLimitDimension.Target"/>) and the permanently silent spray
    /// signal per source (<see cref="RateLimitDimension.Source"/>).</summary>
    Login,
}

/// <summary>
/// The dimensions a policy is limited on. Their roles are fixed and not interchangeable
/// (ADR 0007): <see cref="Target"/> and <see cref="App"/> are the defence,
/// <see cref="Client"/> bounds one integration, <see cref="Source"/> is a coarse anomaly
/// brake sized for shared addresses, <see cref="SourceRegistration"/> is the silent
/// address-spraying ceiling.
/// </summary>
public enum RateLimitDimension
{
    /// <summary>The effective caller address (IPv4 address / IPv6 /64). Loud (429).</summary>
    Source,
    /// <summary>Registration-pipeline entries (unknown address) per source. SILENT: uniform
    /// response, no proof sent — never an existence oracle.</summary>
    SourceRegistration,
    /// <summary>The target identifier — a mailbox or username — regardless of source. Loud.</summary>
    Target,
    /// <summary>The OAuth client (authenticated, or the claimed client_id). Loud.</summary>
    Client,
    /// <summary>The Application (or the realm when none): the global cost brake. Loud.</summary>
    App,
    /// <summary>ADR 0008 — a browser that completed a login before (device cookie), per
    /// user. Only the <c>login</c> policy has it.</summary>
    Device,
}

/// <summary>Whether a realm's limits reject or only observe.</summary>
public enum RateLimitEnforcementMode
{
    Enforce,
    /// <summary>Every dimension is evaluated and counted, would-be rejections are logged,
    /// nothing is rejected. The rollout mode for tuning <c>source</c> against real traffic.</summary>
    LogOnly,
}

/// <summary>
/// One ceiling. <see cref="Burst"/> null = fixed window ("at most <see cref="PermitLimit"/>
/// per <see cref="WindowMinutes"/>"); <see cref="Burst"/> set = token bucket with that
/// capacity, refilled at <see cref="PermitLimit"/> per <see cref="WindowMinutes"/> —
/// absorbs a legitimate peak (an office at 09:00) instead of cutting it off.
/// <see cref="Enabled"/> false turns the dimension off for that policy.
/// </summary>
public record RateLimitRule
{
    public int PermitLimit { get; init; }
    public int WindowMinutes { get; init; }
    public int? Burst { get; init; }
    public bool Enabled { get; init; } = true;

    public bool IsTokenBucket => Burst is > 0;

    public static RateLimitRule Fixed(int limit, int windowMinutes) => new() { PermitLimit = limit, WindowMinutes = windowMinutes };
    public static RateLimitRule Bucket(int limit, int windowMinutes, int burst) => new() { PermitLimit = limit, WindowMinutes = windowMinutes, Burst = burst };
}

/// <summary>Per-policy ceilings. A null dimension inherits the default; a rule with
/// <see cref="RateLimitRule.Enabled"/> = false switches the dimension off.</summary>
public record PolicyLimits
{
    public RateLimitRule? Source { get; init; }
    public RateLimitRule? SourceRegistration { get; init; }
    public RateLimitRule? Target { get; init; }
    public RateLimitRule? Client { get; init; }
    public RateLimitRule? App { get; init; }
    public RateLimitRule? Device { get; init; }

    public RateLimitRule? Get(RateLimitDimension dimension) => dimension switch
    {
        RateLimitDimension.Source => Source,
        RateLimitDimension.SourceRegistration => SourceRegistration,
        RateLimitDimension.Target => Target,
        RateLimitDimension.Client => Client,
        RateLimitDimension.App => App,
        RateLimitDimension.Device => Device,
        _ => null,
    };

    public PolicyLimits With(RateLimitDimension dimension, RateLimitRule? rule) => dimension switch
    {
        RateLimitDimension.Source => this with { Source = rule },
        RateLimitDimension.SourceRegistration => this with { SourceRegistration = rule },
        RateLimitDimension.Target => this with { Target = rule },
        RateLimitDimension.Client => this with { Client = rule },
        RateLimitDimension.App => this with { App = rule },
        RateLimitDimension.Device => this with { Device = rule },
        _ => this,
    };

    public bool IsEmpty => Source is null && SourceRegistration is null && Target is null && Client is null && App is null && Device is null;

    /// <summary>Layer <paramref name="over"/> on top of this: a set dimension in <paramref name="over"/> wins.</summary>
    public PolicyLimits Merge(PolicyLimits? over) => over is null ? this : new PolicyLimits
    {
        Source = over.Source ?? Source,
        SourceRegistration = over.SourceRegistration ?? SourceRegistration,
        Target = over.Target ?? Target,
        Client = over.Client ?? Client,
        App = over.App ?? App,
        Device = over.Device ?? Device,
    };
}

/// <summary>
/// The shipped defaults (ADR 0007). Kept in code so a realm that never touches the
/// feature gets the secure posture; the single source of truth for the evaluator, the
/// admin UI (placeholders / reset target) and the docs.
///
/// <para>Sizing rules: <c>target</c> is the hard line (a mailbox gets a handful of proofs
/// per hour no matter where from); <c>app</c> bounds mail cost; <c>source</c> is a token
/// bucket (1200/h, burst 300) so 1000 users behind one NAT never notice it;
/// <c>source-registration</c> is low and silent.</para>
/// </summary>
public static class AuthRateLimitDefaults
{
    public static readonly IReadOnlyList<AuthRateLimitPolicy> All =
    [
        AuthRateLimitPolicy.NativeOtp, AuthRateLimitPolicy.MagicLink, AuthRateLimitPolicy.PasswordReset,
        AuthRateLimitPolicy.EmailOtp, AuthRateLimitPolicy.EmailVerification, AuthRateLimitPolicy.PasskeyBegin,
        AuthRateLimitPolicy.Bootstrap, AuthRateLimitPolicy.OAuthToken, AuthRateLimitPolicy.SelfRegistration,
        AuthRateLimitPolicy.Login,
    ];

    /// <summary>ADR 0008 — a dimension that is evaluated and counted but can never
    /// reject: the login spray signal per source. A realm admin may tune its
    /// threshold, never turn it into a block (decision 2026-05-07: no address-based
    /// lockout on login).</summary>
    public static bool IsSignalOnly(AuthRateLimitPolicy policy, RateLimitDimension dimension) =>
        policy == AuthRateLimitPolicy.Login && dimension == RateLimitDimension.Source;

    /// <summary>The whole default set for a policy (null dimensions do not apply to it).</summary>
    public static PolicyLimits ForPolicy(AuthRateLimitPolicy policy) => policy switch
    {
        AuthRateLimitPolicy.NativeOtp or AuthRateLimitPolicy.SelfRegistration => new PolicyLimits
        {
            Source = RateLimitRule.Bucket(1200, 60, 300),
            SourceRegistration = RateLimitRule.Fixed(10, 60),
            Target = RateLimitRule.Fixed(5, 60),
            Client = RateLimitRule.Fixed(600, 60),
            App = RateLimitRule.Fixed(3000, 60),
        },
        AuthRateLimitPolicy.MagicLink or AuthRateLimitPolicy.PasswordReset or AuthRateLimitPolicy.EmailVerification => new PolicyLimits
        {
            Source = RateLimitRule.Bucket(1200, 60, 300),
            Target = RateLimitRule.Fixed(5, 60),
            Client = RateLimitRule.Fixed(600, 60),
            App = RateLimitRule.Fixed(3000, 60),
        },
        AuthRateLimitPolicy.EmailOtp => new PolicyLimits
        {
            // Code VERIFY: no mail is sent; the per-challenge attempt cap is the real
            // brute-force defence, these bound a concurrent guess burst.
            Source = RateLimitRule.Bucket(600, 1, 200),
            Target = RateLimitRule.Fixed(15, 1),
            Client = RateLimitRule.Fixed(600, 1),
        },
        AuthRateLimitPolicy.PasskeyBegin => new PolicyLimits
        {
            // Cheap (a challenge + a single-use ceremony doc), no mail.
            Source = RateLimitRule.Bucket(1200, 5, 300),
            Target = RateLimitRule.Fixed(60, 5),
            Client = RateLimitRule.Fixed(1200, 5),
        },
        AuthRateLimitPolicy.Bootstrap => new PolicyLimits
        {
            // One-shot tokens; this is a brake on automated probing of leaked invites.
            Source = RateLimitRule.Bucket(30, 15, 10),
        },
        AuthRateLimitPolicy.OAuthToken => new PolicyLimits
        {
            Source = RateLimitRule.Bucket(600, 1, 200),
            Client = RateLimitRule.Bucket(60, 1, 60),
        },
        AuthRateLimitPolicy.Login => new PolicyLimits
        {
            // ADR 0008: failures, not attempts. Device = a browser the user logged in
            // from before; Target = the untrusted pool per user (what an attacker
            // without the cookie can ever fill); Source = the spray signal, NAT-sized
            // and signal-only (see IsSignalOnly).
            Device = RateLimitRule.Fixed(10, 15),
            Target = RateLimitRule.Fixed(5, 15),
            Source = RateLimitRule.Fixed(200, 15),
        },
        _ => new PolicyLimits { Source = RateLimitRule.Bucket(1200, 60, 300) },
    };

    public static RateLimitRule? For(AuthRateLimitPolicy policy, RateLimitDimension dimension) =>
        ForPolicy(policy).Get(dimension);

    /// <summary>Stable policy name (settings key, metrics tag, 429 body).</summary>
    public static string PolicyName(AuthRateLimitPolicy policy) => policy switch
    {
        AuthRateLimitPolicy.NativeOtp => "native-otp",
        AuthRateLimitPolicy.MagicLink => "magic-link",
        AuthRateLimitPolicy.PasswordReset => "password-reset",
        AuthRateLimitPolicy.EmailOtp => "email-otp",
        AuthRateLimitPolicy.EmailVerification => "email-verification",
        AuthRateLimitPolicy.PasskeyBegin => "passkey-begin",
        AuthRateLimitPolicy.Bootstrap => "bootstrap",
        AuthRateLimitPolicy.OAuthToken => "oauth-token",
        AuthRateLimitPolicy.SelfRegistration => "self-registration",
        AuthRateLimitPolicy.Login => "login",
        _ => policy.ToString().ToLowerInvariant(),
    };

    public static bool TryParse(string? name, out AuthRateLimitPolicy policy)
    {
        foreach (var p in All)
        {
            if (string.Equals(PolicyName(p), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                policy = p;
                return true;
            }
        }
        policy = default;
        return false;
    }
}

/// <summary>
/// Per-realm overrides for the auth rate limits (ADR 0007). Lives as a nullable
/// sub-document on the tenant-DB <see cref="RealmSettings.RealmSettings"/> (null = every
/// policy uses its <see cref="AuthRateLimitDefaults"/>). Only the dimensions an admin
/// actually changed are stored.
///
/// <para><b>Legacy.</b> Before ADR 0007 a realm could set ONE per-IP rule per policy
/// (<see cref="NativeOtp"/> … <see cref="Bootstrap"/>). Those values are NOT migrated
/// into the source ceiling — they were tight only because no other dimension existed.
/// They are kept readable so the admin UI can show them, and a realm that still has any
/// starts in <see cref="RateLimitEnforcementMode.LogOnly"/> until its admin chooses a
/// mode (see <see cref="EffectiveMode"/>).</para>
/// </summary>
public record AuthRateLimitSettings
{
    // ── Legacy single per-IP rules (pre-ADR-0007), read-only compatibility ──────
    public RateLimitRule? NativeOtp { get; init; }
    public RateLimitRule? MagicLink { get; init; }
    public RateLimitRule? PasswordReset { get; init; }
    public RateLimitRule? EmailOtp { get; init; }
    public RateLimitRule? EmailVerification { get; init; }
    public RateLimitRule? PasskeyBegin { get; init; }
    public RateLimitRule? Bootstrap { get; init; }

    /// <summary>Per-policy overrides keyed by <see cref="AuthRateLimitDefaults.PolicyName"/>.</summary>
    public Dictionary<string, PolicyLimits>? Policies { get; init; }

    /// <summary>CIDR ranges (or single addresses) exempt from the SOURCE dimensions only —
    /// a known corporate egress, a known proxy. Target, client and app always apply.</summary>
    public string[]? SourceAllowlist { get; init; }

    public RateLimitEnforcementMode? Mode { get; init; }

    public bool HasLegacyOverrides =>
        NativeOtp is not null || MagicLink is not null || PasswordReset is not null || EmailOtp is not null
        || EmailVerification is not null || PasskeyBegin is not null || Bootstrap is not null;

    /// <summary>Nothing stored at all — the realm/App is on shipped defaults.</summary>
    public bool IsEmpty => !HasLegacyOverrides && (Policies is null || Policies.Count == 0) && SourceAllowlist is null && Mode is null;

    public PolicyLimits? GetPolicy(AuthRateLimitPolicy policy) =>
        Policies is not null && Policies.TryGetValue(AuthRateLimitDefaults.PolicyName(policy), out var limits) ? limits : null;

    /// <summary>The effective rule: override if set, else the shipped default; null when
    /// the dimension does not apply to the policy.</summary>
    public static RateLimitRule? Effective(AuthRateLimitSettings? settings, AuthRateLimitPolicy policy, RateLimitDimension dimension) =>
        settings?.GetPolicy(policy)?.Get(dimension) ?? AuthRateLimitDefaults.For(policy, dimension);

    public static PolicyLimits EffectivePolicy(AuthRateLimitSettings? settings, AuthRateLimitPolicy policy) =>
        AuthRateLimitDefaults.ForPolicy(policy).Merge(settings?.GetPolicy(policy));

    public static RateLimitEnforcementMode EffectiveMode(AuthRateLimitSettings? settings) =>
        settings?.Mode ?? (settings?.HasLegacyOverrides == true ? RateLimitEnforcementMode.LogOnly : RateLimitEnforcementMode.Enforce);

    public static IReadOnlyList<string> EffectiveAllowlist(AuthRateLimitSettings? settings) =>
        settings?.SourceAllowlist ?? [];

    /// <summary>Layer Application overrides on top of the realm settings.</summary>
    public static AuthRateLimitSettings? Merge(AuthRateLimitSettings? realm, AuthRateLimitSettings? app)
    {
        if (app is null) return realm;
        if (realm is null) return app;
        var policies = new Dictionary<string, PolicyLimits>(realm.Policies ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var (name, over) in app.Policies ?? new())
            policies[name] = (policies.TryGetValue(name, out var existing) ? existing : new PolicyLimits()).Merge(over);
        return realm with
        {
            Policies = policies,
            SourceAllowlist = app.SourceAllowlist ?? realm.SourceAllowlist,
            Mode = app.Mode ?? realm.Mode,
        };
    }
}
