namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the account-deletion sub-section of
/// <c>/api/admin/realm-settings</c>. Defaults are surfaced for realms where
/// the policy has never been configured, so the SPA can render the edit form
/// without special-casing a null section.</summary>
public record DeletionSettingsDto
{
    public int GraceDays { get; init; } = 30;
    public int ReminderLeadDays { get; init; } = 2;
    public int AdminRetentionDays { get; init; } = 30;
    public bool AutoPurgeEnabled { get; init; } = true;
}

/// <summary>Patch payload for the account-deletion sub-section. Each property
/// is nullable = no change on the wire; non-null = replace. Same partial-PATCH
/// shape as the other sub-sections.</summary>
public record UpdateDeletionSettingsDto
{
    public int? GraceDays { get; init; }
    public int? ReminderLeadDays { get; init; }
    public int? AdminRetentionDays { get; init; }
    public bool? AutoPurgeEnabled { get; init; }
}
