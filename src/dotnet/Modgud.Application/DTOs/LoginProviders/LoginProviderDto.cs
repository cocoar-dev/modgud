using System.Text.Json;

namespace Modgud.Application.DTOs.LoginProviders;

/// <summary>
/// Admin-facing representation of a login provider. NEVER includes the secret
/// — only a boolean indicating whether one is set, so the admin UI can decide
/// whether to render "Set secret" vs "Rotate secret".
/// <para>
/// <see cref="Type"/> is the string form of the
/// <c>Modgud.Authentication.Domain.LoginProviders.LoginProviderType</c>
/// enum (e.g. <c>"Internal"</c>, <c>"Oidc"</c>) — kept as a string at the DTO
/// boundary so the Application layer doesn't need to reference the
/// Authentication slice. For <c>Internal</c> entries the OIDC-specific
/// fields (ClientId, scopes, FlavorData, etc.) are returned as their domain
/// defaults (empty string / empty list / null) — the admin UI is expected to
/// branch on <see cref="Type"/> and hide them.
/// </para>
/// </summary>
public record LoginProviderDto
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Flavor { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required bool IsBuiltIn { get; init; }
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

    /// <summary>Full redirect URI that the admin copies into the IdP app registration. Empty for Internal-typed providers.</summary>
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
