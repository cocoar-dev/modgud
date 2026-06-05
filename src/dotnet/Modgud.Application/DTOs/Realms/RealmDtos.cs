namespace Modgud.Application.DTOs.Realms;

public record RealmDto
{
    public Guid Id { get; init; }
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string[] Domains { get; init; } = [];

    /// <summary>The realm's canonical public host — one of <see cref="Domains"/>.
    /// Used for all outbound links and as the WebAuthn RP ID.</summary>
    public string PrimaryDomain { get; init; } = string.Empty;
    public bool IsControlPlane { get; init; }
    public bool IsActive { get; init; }
    public bool NeedsSetup { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Read shape for the per-realm self-registration settings.
/// The captcha-secret is never returned — only a boolean
/// <see cref="CaptchaSecretSet"/> flag so the admin UI can show
/// "configured" vs "not configured" without ever shipping the plaintext
/// to the client.</summary>
public record SelfRegistrationDto
{
    public bool Enabled { get; init; }
    public bool RequireEmailVerification { get; init; } = true;
    public string[]? AllowedEmailDomains { get; init; }
    public bool RequireAdminApproval { get; init; }
    public string[]? DefaultGroupIds { get; init; }
    public string? TermsOfServiceUrl { get; init; }
    public string? PrivacyPolicyUrl { get; init; }

    /// <summary>Captcha master toggle. When <c>false</c>, captcha is
    /// skipped at /register entirely — useful for intern / air-gapped
    /// deployments. When <c>true</c>, a valid key-pair must resolve via
    /// per-realm settings or the Cocoar-default fallback.</summary>
    public bool CaptchaEnabled { get; init; }

    /// <summary>Per-realm Turnstile site-key (public). Null = use the
    /// Cocoar-default site-key from system configuration.</summary>
    public string? CaptchaSiteKey { get; init; }

    /// <summary><c>true</c> when this realm has its own
    /// captcha-secret stored encrypted-at-rest; <c>false</c> = falls
    /// through to the Cocoar-default secret. The plaintext is never
    /// surfaced.</summary>
    public bool CaptchaSecretSet { get; init; }
}

/// <summary>Patch payload for the self-registration settings. PATCH
/// semantics throughout: <c>null</c>/missing = no change; setting a
/// nullable field to its default-ish value (empty array, empty string)
/// = clear. The captcha-secret has three states:
/// <list type="bullet">
///   <item><c>null</c> = no change</item>
///   <item>empty string = clear (revert to Cocoar-default secret)</item>
///   <item>non-empty string = replace with this value (encrypted at rest)</item>
/// </list></summary>
public record UpdateSelfRegistrationDto
{
    public bool? Enabled { get; init; }
    public bool? RequireEmailVerification { get; init; }
    public string[]? AllowedEmailDomains { get; init; }
    public bool? RequireAdminApproval { get; init; }
    public string[]? DefaultGroupIds { get; init; }
    public string? TermsOfServiceUrl { get; init; }
    public string? PrivacyPolicyUrl { get; init; }
    public bool? CaptchaEnabled { get; init; }
    public string? CaptchaSiteKey { get; init; }
    public string? CaptchaSecret { get; init; }
}

public record CreateRealmDto
{
    public string Slug { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Description { get; init; }

    /// <summary>
    /// Routing domains for the realm. REQUIRED — a realm with no domain
    /// cannot route requests or build outbound links. Provide at least one.
    /// </summary>
    public string[]? Domains { get; init; }

    /// <summary>
    /// Optional. The canonical public host for outbound links + WebAuthn RP.
    /// When set it must be one of <see cref="Domains"/>; when omitted the
    /// first entry of <see cref="Domains"/> is used.
    /// </summary>
    public string? PrimaryDomain { get; init; }

    /// <summary>
    /// First-admin invite issued atomically with the realm (C15c).
    /// Required: a realm with no admin path is unusable. The CP-admin
    /// fills UserName + Email; the recipient gets a magic-link mail and
    /// sets their own password — the CP-admin never sees the password,
    /// which keeps SaaS scenarios clean (tenant requester is the only
    /// person who knows the credentials).
    /// </summary>
    public InitialAdminDto InitialAdmin { get; init; } = new();
}

public record InitialAdminDto
{
    public string UserName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
}

public record CreatedRealmDto
{
    public RealmDto Realm { get; init; } = new();

    /// <summary>
    /// Bootstrap-invite metadata returned to the CP-admin who issued the
    /// realm. Includes the magic-link URL — useful in SMTP-less dev
    /// setups where the email isn't actually delivered. The token's
    /// plaintext is part of the URL; the CP-admin should treat this as
    /// secret-equivalent and either copy it to a secure channel or trust
    /// that the recipient will get the email.
    /// </summary>
    public InitialAdminInviteDto InitialAdminInvite { get; init; } = new();
}

public record InitialAdminInviteDto
{
    public string UserName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public string MagicLinkUrl { get; init; } = string.Empty;
}

public record UpdateRealmDto
{
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string[]? Domains { get; init; }

    /// <summary>
    /// Optional. When set it must be one of the resulting domain set (the
    /// new <see cref="Domains"/> if provided, otherwise the realm's current
    /// domains). Changing it invalidates the realm's existing passkeys.
    /// </summary>
    public string? PrimaryDomain { get; init; }
    public bool? IsActive { get; init; }
}

public record RealmListDto
{
    public List<RealmDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
}
