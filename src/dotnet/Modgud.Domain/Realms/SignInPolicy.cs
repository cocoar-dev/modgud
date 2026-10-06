namespace Modgud.Domain.Realms;

/// <summary>
/// ADR 0025 — the strength a sign-in reaches. Ordered: a higher value satisfies
/// every lower requirement.
/// </summary>
public enum SignInLevel
{
    /// <summary>One factor: a password, an e-mail code or a magic link.</summary>
    Single = 1,

    /// <summary>Two different kinds of proof, or one proof that is multi-factor on
    /// its own (a user-verified passkey, a federated sign-in that asserted MFA).</summary>
    Multi = 2,
}

/// <summary>
/// ADR 0025 §7 — what an App does when a user switched on a second factor of their
/// own that the App does not offer.
/// </summary>
public enum OwnFactorNotOffered
{
    /// <summary>Only the App's minimum level applies; the user's own factor is not asked for.</summary>
    Ignore = 0,

    /// <summary>The native sign-in answers <c>mfa_required</c> with a continuation URL,
    /// and the login page asks for the missing factor in a browser.</summary>
    RequireViaBrowser = 1,
}

/// <summary>
/// ADR 0025 — the realm's sign-in policy, overridable per App
/// (<see cref="Applications.ApplicationSignInOverrides"/>). Replaces the deployment-wide
/// <c>AuthenticationMinimumLevel</c> / <c>TwoFactorGracePeriodDays</c>: sign-in strength
/// is a realm and App concern.
/// </summary>
public record SignInPolicy
{
    /// <summary>The level a sign-in to this App must reach.</summary>
    public SignInLevel MinimumLevel { get; init; } = SignInLevel.Single;

    /// <summary>Realm-level only: the level a session must reach to use the realm's
    /// administration (<c>/api/admin/*</c>). Separate from <see cref="MinimumLevel"/>
    /// because the Modgud UI is both the admin console and every user's self-service
    /// portal — end users of a code-only App must not be pushed into 2FA by opening their
    /// profile. App overrides do not touch it.</summary>
    public SignInLevel AdministrationMinimumLevel { get; init; } = SignInLevel.Multi;

    /// <summary>Days a user who has no second factor yet may keep signing in at
    /// <see cref="SignInLevel.Single"/> to an App that requires <see cref="SignInLevel.Multi"/>.
    /// Starts at their first such sign-in. 0 = set up immediately.</summary>
    public int SetupGraceDays { get; init; } = 14;

    // ── Sign-in methods the App offers (first factors) ──

    /// <summary>Username/e-mail + password.</summary>
    public bool Password { get; init; } = true;

    /// <summary>A one-time code sent to the e-mail address, as the first factor — on the
    /// login page and through the native <c>urn:cocoar:otp</c> grant.</summary>
    public bool EmailCode { get; init; }

    /// <summary>Passkey sign-in (multi-factor on its own).</summary>
    public bool Passkey { get; init; } = true;

    // ── Second factors the App offers ──

    /// <summary>An authenticator-app code (TOTP) after the first factor.</summary>
    public bool Totp { get; init; } = true;

    /// <summary>An e-mail code after a password. Never offered after an e-mail-code or
    /// magic-link sign-in: it would prove the same mailbox twice.</summary>
    public bool EmailAfterPassword { get; init; } = true;

    /// <summary>What happens when a user has their own second factor that this App
    /// does not offer.</summary>
    public OwnFactorNotOffered OwnFactorNotOffered { get; init; } = OwnFactorNotOffered.RequireViaBrowser;

    public static SignInPolicy Defaults { get; } = new();

    /// <summary>
    /// The policy a realm that has never configured <see cref="RealmSettings.RealmSettings.SignIn"/>
    /// runs under: derived from the deployment's former settings, so an upgrade changes
    /// nothing until an admin saves the section. <paramref name="legacyAuthenticationLevel"/>
    /// is the retired <c>AuthenticationMinimumLevel</c> (0 none, 1 secure login,
    /// 2 passwordless); <paramref name="emailCodeEnabled"/> is whether the realm had
    /// native grants switched on — until now the gate for e-mail-code sign-in.
    /// </summary>
    public static SignInPolicy FromLegacy(int legacyAuthenticationLevel, int legacyGraceDays, bool emailCodeEnabled) => new()
    {
        MinimumLevel = legacyAuthenticationLevel >= 1 ? SignInLevel.Multi : SignInLevel.Single,
        AdministrationMinimumLevel = legacyAuthenticationLevel >= 1 ? SignInLevel.Multi : SignInLevel.Single,
        SetupGraceDays = Math.Max(0, legacyGraceDays),
        Password = legacyAuthenticationLevel < 2,
        EmailCode = emailCodeEnabled,
        // Existing deployments keep their behaviour for users with their own TOTP in
        // apps that do not offer it: today the native grant demands it, the web login
        // asks for it — the browser continuation is the closest successor.
        OwnFactorNotOffered = OwnFactorNotOffered.RequireViaBrowser,
    };

    /// <summary>Whether at least one way to reach <see cref="SignInLevel.Multi"/> is offered:
    /// a passkey, or a first factor followed by a second factor that differs from it.</summary>
    public bool CanReachMulti =>
        Passkey
        || (Totp && (Password || EmailCode))
        || (EmailAfterPassword && Password);
}
