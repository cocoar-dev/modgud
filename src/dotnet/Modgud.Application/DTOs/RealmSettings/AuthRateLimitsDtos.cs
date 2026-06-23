namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>A single rate-limit ceiling on the wire: at most <see cref="PermitLimit"/>
/// requests per <see cref="WindowMinutes"/> from one source IP (per realm).</summary>
public record RateLimitRuleDto
{
    public int PermitLimit { get; init; }
    public int WindowMinutes { get; init; }
}

/// <summary>Read shape for the per-realm auth rate-limit ceilings. Every field is
/// non-null = the EFFECTIVE rule (the realm override if set, else the shipped
/// default), so the SPA renders concrete numbers without knowing the defaults.</summary>
public record AuthRateLimitsDto
{
    public RateLimitRuleDto NativeOtp { get; init; } = new();
    public RateLimitRuleDto MagicLink { get; init; } = new();
    public RateLimitRuleDto PasswordReset { get; init; } = new();
    public RateLimitRuleDto EmailOtp { get; init; } = new();
    public RateLimitRuleDto EmailVerification { get; init; } = new();
    public RateLimitRuleDto PasskeyBegin { get; init; } = new();
    public RateLimitRuleDto Bootstrap { get; init; } = new();
}

/// <summary>Patch payload: a null policy field = no change; a non-null rule
/// replaces that policy's ceiling (stored as a realm override). Setting a rule
/// back to the default values simply stores those values — functionally identical
/// to inheriting, so there is no separate "reset" verb.</summary>
public record UpdateAuthRateLimitsDto
{
    public RateLimitRuleDto? NativeOtp { get; init; }
    public RateLimitRuleDto? MagicLink { get; init; }
    public RateLimitRuleDto? PasswordReset { get; init; }
    public RateLimitRuleDto? EmailOtp { get; init; }
    public RateLimitRuleDto? EmailVerification { get; init; }
    public RateLimitRuleDto? PasskeyBegin { get; init; }
    public RateLimitRuleDto? Bootstrap { get; init; }
}
