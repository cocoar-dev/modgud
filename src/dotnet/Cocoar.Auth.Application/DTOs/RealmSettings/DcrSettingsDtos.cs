namespace Cocoar.Auth.Application.DTOs.RealmSettings;

/// <summary>Read shape for the Dynamic-Client-Registration sub-section of
/// <c>/api/admin/realm-settings</c>. Defaults are surfaced for realms
/// where the feature has never been touched — the SPA can render the
/// edit form without first having to special-case a null section.</summary>
public record DcrSettingsDto
{
    public bool Enabled { get; init; }
    public int AccessTokenLifetimeMinutes { get; init; } = 15;
    public int RefreshTokenLifetimeDays { get; init; } = 7;
    public int GcTtlDays { get; init; } = 90;
    public int PerIpRateLimitPerHour { get; init; } = 5;
    public int PerRealmRateLimitPerDay { get; init; } = 100;
    public string[]? ReservedNames { get; init; }
}

/// <summary>Patch payload for the DCR sub-section. Each property is
/// nullable = no change on the wire; non-null = replace. Same partial-PATCH
/// shape as the self-registration sub-section.</summary>
public record UpdateDcrSettingsDto
{
    public bool? Enabled { get; init; }
    public int? AccessTokenLifetimeMinutes { get; init; }
    public int? RefreshTokenLifetimeDays { get; init; }
    public int? GcTtlDays { get; init; }
    public int? PerIpRateLimitPerHour { get; init; }
    public int? PerRealmRateLimitPerDay { get; init; }
    public string[]? ReservedNames { get; init; }
}
