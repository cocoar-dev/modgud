using Modgud.Domain.Common;
using Modgud.Domain.Realms;

namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>One ceiling on the wire. <see cref="Burst"/> null = fixed window; set = token
/// bucket with that capacity refilled at <see cref="PermitLimit"/> per window.
/// <see cref="Enabled"/> false switches the dimension off.</summary>
public record RateLimitRuleDto
{
    public int PermitLimit { get; init; }
    public int WindowMinutes { get; init; }
    public int? Burst { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Read-only marker (ADR 0020): the dimension is evaluated and counted but
    /// never rejects (the login spray signal). Ignored on write.</summary>
    public bool SignalOnly { get; init; }
}

/// <summary>Read shape of one policy: the EFFECTIVE rule per dimension (override if set,
/// else the shipped default); null = the dimension does not apply to the policy.</summary>
public record PolicyLimitsDto
{
    public RateLimitRuleDto? Source { get; init; }
    public RateLimitRuleDto? SourceRegistration { get; init; }
    public RateLimitRuleDto? Target { get; init; }
    public RateLimitRuleDto? Client { get; init; }
    public RateLimitRuleDto? App { get; init; }
    public RateLimitRuleDto? Device { get; init; }
}

/// <summary>Read shape (ADR 0019). <see cref="Policies"/> carries every policy with
/// effective values so the SPA renders concrete numbers; <see cref="Overrides"/> is what
/// is actually stored (sparse) — the export/import shape and the "reset" reference.</summary>
public record AuthRateLimitsDto
{
    public Dictionary<string, PolicyLimitsDto> Policies { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shipped defaults per policy, so a UI can show what "inherit" means and
    /// offer a reset without knowing the numbers.</summary>
    public Dictionary<string, PolicyLimitsDto> Defaults { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string[] SourceAllowlist { get; init; } = [];
    public RateLimitEnforcementMode Mode { get; init; }

    /// <summary>The realm still carries pre-ADR-0019 single per-IP rules; until an admin
    /// picks a mode explicitly it runs log-only.</summary>
    public bool LegacyOverridesPresent { get; init; }

    public UpdateAuthRateLimitsDto? Overrides { get; init; }
}

/// <summary>Merge-patch v2 per dimension: absent = unchanged, explicit null = back to the
/// shipped default, value = override.</summary>
public record UpdatePolicyLimitsDto
{
    public Optional<RateLimitRuleDto?> Source { get; init; }
    public Optional<RateLimitRuleDto?> SourceRegistration { get; init; }
    public Optional<RateLimitRuleDto?> Target { get; init; }
    public Optional<RateLimitRuleDto?> Client { get; init; }
    public Optional<RateLimitRuleDto?> App { get; init; }
    public Optional<RateLimitRuleDto?> Device { get; init; }
}

/// <summary>Patch payload (realm) / sparse override (Application, manifest). Keys of
/// <see cref="Policies"/> are policy names (<c>native-otp</c>, …); a null value drops every
/// override of that policy. <see cref="SourceAllowlist"/> null = clear; <see cref="Mode"/>
/// null = automatic (enforce, or log-only while legacy rules are present).</summary>
public record UpdateAuthRateLimitsDto
{
    public Dictionary<string, UpdatePolicyLimitsDto?>? Policies { get; init; }
    public Optional<string[]?> SourceAllowlist { get; init; }
    public Optional<RateLimitEnforcementMode?> Mode { get; init; }

    /// <summary>Drop the pre-ADR-0019 single per-IP rules.</summary>
    public bool? ClearLegacy { get; init; }

    // ── pre-ADR-0019 shape, accepted for manifest compatibility ──────────────
    // A value stores a LEGACY override (single per-IP rule) — it is not migrated
    // into the source ceiling and puts the realm into log-only mode until an admin
    // chooses a mode. New configuration should use Policies.
    public RateLimitRuleDto? NativeOtp { get; init; }
    public RateLimitRuleDto? MagicLink { get; init; }
    public RateLimitRuleDto? PasswordReset { get; init; }
    public RateLimitRuleDto? EmailOtp { get; init; }
    public RateLimitRuleDto? EmailVerification { get; init; }
    public RateLimitRuleDto? PasskeyBegin { get; init; }
    public RateLimitRuleDto? Bootstrap { get; init; }
}
