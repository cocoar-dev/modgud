namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the Client-ID-Metadata-Document sub-section of
/// <c>/api/admin/realm-settings</c>. Defaults are surfaced for
/// realms where the feature has never been touched so the SPA can render the
/// edit form without special-casing a null section.</summary>
public record CimdSettingsDto
{
    public bool Enabled { get; init; }
    public int AccessTokenLifetimeMinutes { get; init; } = 15;
    public int RefreshTokenLifetimeDays { get; init; } = 7;
}

/// <summary>Patch payload for the CIMD sub-section. Each property is
/// nullable = no change on the wire; non-null = replace. Same partial-PATCH
/// shape as the DCR sub-section.</summary>
public record UpdateCimdSettingsDto
{
    public bool? Enabled { get; init; }
    public int? AccessTokenLifetimeMinutes { get; init; }
    public int? RefreshTokenLifetimeDays { get; init; }
}
