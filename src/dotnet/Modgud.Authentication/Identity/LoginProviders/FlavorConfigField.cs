namespace Modgud.Authentication.Identity.LoginProviders;

/// <summary>
/// Describes one flavor-specific configuration field. Consumed by the admin UI
/// to render the appropriate input for a flavor's connection panel (e.g. Entra
/// shows a <c>TenantId</c> input; Generic OIDC shows a <c>MetadataUri</c> input).
/// </summary>
public record FlavorConfigField(
    string Key,
    FlavorConfigFieldType Type,
    string Label,
    bool Required = false,
    string? HelpText = null,
    string? Placeholder = null,
    object? Default = null);

public enum FlavorConfigFieldType
{
    String,
    Url,
    MultilineText,
    Boolean,
    Secret,
    StringList,
}
