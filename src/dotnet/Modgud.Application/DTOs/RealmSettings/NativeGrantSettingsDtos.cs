namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the native-passwordless-grants sub-section of
/// <c>/api/admin/realm-settings</c> (ADR-0010). Defaults are surfaced for realms
/// where the feature has never been touched so the SPA can render the edit form
/// without special-casing a null section.</summary>
public record NativeGrantSettingsDto
{
    public bool Enabled { get; init; }
    public int AccessTokenLifetimeMinutes { get; init; } = 15;
}

/// <summary>Patch payload for the native-grants sub-section. Each property is
/// nullable = no change on the wire; non-null = replace. Same partial-PATCH shape
/// as the DCR/CIMD sub-sections. There is no refresh-token lifetime: a native refresh
/// token follows the client-session idle lifetime; a stale RefreshTokenLifetimeDays on
/// the wire is ignored.</summary>
public record UpdateNativeGrantSettingsDto
{
    public bool? Enabled { get; init; }
    public int? AccessTokenLifetimeMinutes { get; init; }
}
