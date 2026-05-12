namespace Cocoar.Auth.Domain.Realms;

/// <summary>
/// Per-realm configuration for the public self-registration flow
/// (<c>POST /api/account/register</c>). A sub-document on the
/// tenant-DB <c>RealmSettings</c> aggregate — owned by the realm-admin,
/// not by Control-Plane. Opt-in by default (every realm starts with
/// <c>Enabled=false</c> and the registration endpoint returns the
/// anti-enumeration "feature absent" response).
///
/// <para>The captcha integration is opt-in at two levels: a Cocoar-wide
/// default Turnstile key-pair lives in <c>StartUpConfiguration</c>
/// (env-var configured); per-realm overrides land in
/// <see cref="CaptchaSiteKey"/> /
/// <see cref="EncryptedCaptchaSecret"/>. A realm with no override picks
/// up the cocoar-default. A realm with no key-pair anywhere goes through
/// the honeypot + email-rate-limit path only.</para>
///
/// <para>Stored as a JSONB sub-document on the tenant-DB
/// <c>RealmSettings</c> record — adding fields here doesn't need a
/// schema migration.</para>
/// </summary>
public record SelfRegistrationSettings
{
    /// <summary>
    /// Master toggle. When <c>false</c>, both the public register endpoint
    /// AND the "is self-registration enabled here?" probe respond as if the
    /// feature doesn't exist — no info-disclosure to drive-by visitors.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// When <c>true</c> (default), the user's account is created with
    /// <c>EmailConfirmed=false</c> and a magic-link verification email is
    /// sent; the account cannot sign in until the link is clicked. When
    /// <c>false</c>, the user can sign in immediately — only set this for
    /// trusted-internal scenarios.
    /// </summary>
    public bool RequireEmailVerification { get; init; } = true;

    /// <summary>
    /// Optional email-domain allow-list. Empty/null = accept any email
    /// domain. Case-insensitive match against the email's domain part
    /// (everything after the last <c>@</c>).
    /// </summary>
    public string[]? AllowedEmailDomains { get; init; }

    /// <summary>
    /// When <c>true</c>, a registered user is created in pending state and
    /// cannot sign in until an admin approves them in the admin UI. Layered
    /// on top of email-verification: verification happens first, then the
    /// user waits for approval.
    /// </summary>
    public bool RequireAdminApproval { get; init; }

    /// <summary>
    /// Group <see cref="Guid"/> strings to auto-attach the new user to once
    /// the account is fully active (post-verification and post-approval).
    /// Role membership flows through groups in this model — leaving this
    /// empty means the user lands with zero roles, which is a valid choice
    /// (e.g. for a self-onboarded "anonymous-tier" account).
    /// </summary>
    public string[]? DefaultGroupIds { get; init; }

    /// <summary>Optional Terms-of-Service URL to render next to a required
    /// "I accept"-checkbox on the registration form. When set, the form
    /// MUST send back <c>AcceptedTerms=true</c> or registration is
    /// rejected.</summary>
    public string? TermsOfServiceUrl { get; init; }

    /// <summary>Optional Privacy-Policy URL rendered as a footer link on the
    /// registration form. No checkbox attached — just visibility.</summary>
    public string? PrivacyPolicyUrl { get; init; }

    /// <summary>
    /// Captcha master toggle — independent of <see cref="Enabled"/>. Lets
    /// air-gapped / intern-deployment realms run public self-registration
    /// without ever calling out to Cloudflare. When <c>false</c>, the
    /// register endpoint skips captcha verification entirely; honeypot +
    /// email rate-limit cover the bot-spam surface. When <c>true</c>,
    /// captcha is mandatory and must resolve to a valid key-pair via the
    /// fallback chain (per-realm → Cocoar-default → validation error).
    /// </summary>
    public bool CaptchaEnabled { get; init; }

    /// <summary>
    /// Per-realm Cloudflare Turnstile site-key. Public value — safe to
    /// include in the <c>/api/account/self-registration-info</c> response
    /// because the SPA needs it to mount the widget. Pairs with
    /// <see cref="EncryptedCaptchaSecret"/>. Both null while
    /// <see cref="CaptchaEnabled"/>=<c>true</c> falls through to the
    /// Cocoar-default keys in system configuration; if those are also
    /// absent, PATCH-time validation rejects the change so the admin
    /// can't end up with a "captcha-required but no keys configured"
    /// state.
    /// </summary>
    public string? CaptchaSiteKey { get; init; }

    /// <summary>
    /// Per-realm Cloudflare Turnstile secret-key, encrypted at rest with
    /// the same data-protection purpose pattern as login-provider client
    /// secrets. The endpoint that surfaces self-registration-info NEVER
    /// returns this — only an "is-configured" flag.
    /// </summary>
    public byte[]? EncryptedCaptchaSecret { get; init; }
}
