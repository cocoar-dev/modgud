namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the tenant-audit sub-section of
/// <c>/api/admin/realm-settings</c>. Defaults are surfaced for realms where the
/// window has never been configured, so the SPA renders the edit form without
/// special-casing a null section.</summary>
public record AuditSettingsDto
{
    public int VisibilityWindowDays { get; init; } = 90;
}

/// <summary>Patch payload for the tenant-audit sub-section. Nullable = no change on
/// the wire; non-null = replace. Same partial-PATCH shape as the other
/// sub-sections.</summary>
public record UpdateAuditSettingsDto
{
    public int? VisibilityWindowDays { get; init; }
}
