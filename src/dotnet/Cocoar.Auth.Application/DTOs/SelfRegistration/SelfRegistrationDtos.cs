namespace Cocoar.Auth.Application.DTOs.SelfRegistration;

/// <summary>
/// Public-shape config the SPA fetches before rendering /register. Only
/// what's safe to expose anonymously — no secrets, no realm internals,
/// no info-disclosure when <see cref="Enabled"/>=false (the endpoint
/// returns the all-defaults version so a drive-by can't probe per-realm
/// self-reg toggles).
/// </summary>
public record SelfRegistrationInfoDto
{
    public bool Enabled { get; init; }
    public bool RequireEmailVerification { get; init; }
    public bool RequireAdminApproval { get; init; }
    public string[]? AllowedEmailDomains { get; init; }
    public string? TermsOfServiceUrl { get; init; }
    public string? PrivacyPolicyUrl { get; init; }

    /// <summary>Captcha is required on submit when this is non-null.
    /// SPA mounts the Turnstile widget with this site-key.</summary>
    public string? CaptchaSiteKey { get; init; }
}

/// <summary>POST body for /api/account/register. All admin-side
/// validation lives in <c>SelfRegistrationService</c> — this DTO is
/// just the wire shape.</summary>
public record RegisterDto
{
    public string UserName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }

    /// <summary>The ToS-accept checkbox value. The service only checks
    /// this when the realm settings carry a <c>TermsOfServiceUrl</c>;
    /// without a ToS URL, this field is ignored.</summary>
    public bool AcceptedTerms { get; init; }

    /// <summary>Cloudflare Turnstile widget token, set when the realm has
    /// CaptchaEnabled=true. Server-side <c>siteverify</c> consumes it
    /// exactly once (single-use, ~5 min validity).</summary>
    public string? CaptchaToken { get; init; }

    /// <summary>Honeypot — a hidden form field bots blindly fill. When
    /// non-empty the server quietly rejects (still 200 OK to keep the
    /// bot in the dark).</summary>
    public string? Honeypot { get; init; }
}

/// <summary>Generic response — the same shape regardless of whether the
/// email was already taken or the registration actually went through.
/// Anti-enumeration: don't tell an attacker which emails have accounts.
/// The "wait for verification" message is also fine when no email was
/// sent — the recipient just never gets one and never proceeds.</summary>
public record RegisterResponseDto
{
    public required string Message { get; init; }
}
