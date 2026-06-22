namespace Modgud.Application.DTOs.RealmSettings;

/// <summary>Read shape for the registration-fields sub-section of
/// <c>/api/admin/realm-settings</c>. Email is always required and is not
/// represented. Defaults (all <c>Optional</c> — today's lenient behaviour) are
/// surfaced for realms where the policy has never been configured, so the SPA
/// can render the edit form without special-casing a null section. Each value
/// is one of <c>Off</c> / <c>Optional</c> / <c>Required</c>.</summary>
public record RegistrationFieldsSettingsDto
{
    public string Username { get; init; } = "Optional";
    public string Firstname { get; init; } = "Optional";
    public string Lastname { get; init; } = "Optional";
}

/// <summary>Patch payload for the registration-fields sub-section. Each property
/// is nullable = no change on the wire; non-null = replace with one of
/// <c>Off</c> / <c>Optional</c> / <c>Required</c>.</summary>
public record UpdateRegistrationFieldsSettingsDto
{
    public string? Username { get; init; }
    public string? Firstname { get; init; }
    public string? Lastname { get; init; }
}
