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
    object? Default = null,
    string Section = FlavorConfigSections.Connection,
    IReadOnlyList<FlavorConfigFieldOption>? Options = null);

/// <summary>One choice for a <see cref="FlavorConfigFieldType.Select"/> field.</summary>
public record FlavorConfigFieldOption(string Value, string Label);

/// <summary>
/// Logical grouping a <see cref="FlavorConfigField"/> belongs to. The admin UI
/// renders each section in its own tab so common setup stays simple while every
/// advanced knob remains reachable for fine-tuning.
/// </summary>
public static class FlavorConfigSections
{
    public const string Connection = "connection";
    public const string Advanced = "advanced";
}

public enum FlavorConfigFieldType
{
    String,
    Url,
    MultilineText,
    Boolean,
    Secret,
    StringList,
    Select,
}
