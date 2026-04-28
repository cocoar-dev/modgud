using System.Text.Json;

namespace TimeToDo.Application.DTOs.ExternalAuth;

/// <summary>
/// Admin-facing representation of an IdP config. NEVER includes the secret —
/// only a boolean indicating whether one is set, so the admin UI can decide
/// whether to render "Set secret" vs "Rotate secret".
/// </summary>
public record IdpConfigDto
{
    public required string Id { get; init; }
    public required string Flavor { get; init; }
    public required string DisplayName { get; init; }
    public required bool Enabled { get; init; }
    public required string ClientId { get; init; }
    public required bool HasClientSecret { get; init; }
    public required List<string> Scopes { get; init; }
    public required string UserUpdateScript { get; init; }
    public required bool StoreRawClaims { get; init; }
    public int? RawClaimsRetentionDays { get; init; }
    public required bool AutoCreateUsers { get; init; }
    public required bool AllowLinking { get; init; }
    public required bool TrustForEmailLink { get; init; }
    public List<string>? AllowedEmailDomains { get; init; }
    public string? IconName { get; init; }
    public string? ButtonColorHex { get; init; }
    public JsonElement? FlavorData { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Full redirect URI that the admin copies into the IdP app registration.</summary>
    public required string RedirectUri { get; init; }
}

/// <summary>Flavor metadata for the "Add provider" picker.</summary>
public record FlavorDto
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string DefaultIconName { get; init; }
    public required List<string> DefaultScopes { get; init; }
    public required string DefaultUserUpdateScript { get; init; }
    public required bool DefaultStoreRawClaims { get; init; }
    public required List<FlavorConfigFieldDto> ConfigSchema { get; init; }
}

public record FlavorConfigFieldDto(
    string Key,
    string Type,
    string Label,
    bool Required,
    string? HelpText,
    string? Placeholder);
