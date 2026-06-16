namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the native-passwordless-grants sub-section of
/// <c>/api/admin/realm-settings</c> (ADR-0010). Defaults are surfaced for realms
/// where the feature has never been touched so the SPA can render the edit form
/// without special-casing a null section.</summary>
public record NativeGrantSettingsDto
{
    public bool Enabled { get; init; }
    public int AccessTokenLifetimeMinutes { get; init; } = 15;
    public int RefreshTokenLifetimeDays { get; init; } = 14;
}

/// <summary>Patch payload for the native-grants sub-section. Each property is
/// nullable = no change on the wire; non-null = replace. Same partial-PATCH shape
/// as the DCR/CIMD sub-sections.</summary>
public record UpdateNativeGrantSettingsDto
{
    public bool? Enabled { get; init; }
    public int? AccessTokenLifetimeMinutes { get; init; }
    public int? RefreshTokenLifetimeDays { get; init; }
}
