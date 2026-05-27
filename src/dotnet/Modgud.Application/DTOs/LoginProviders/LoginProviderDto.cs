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

    /// <summary>
    /// URL-stable, admin-chosen identifier used in the provider's public URLs
    /// (OIDC callback, SAML SP surface). Set at creation, immutable thereafter.
    /// </summary>
    public required string Slug { get; init; }
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

    /// <summary>OIDC redirect URI the admin copies into the IdP app registration. Empty for non-OIDC providers (Internal, SAML).</summary>
    public required string RedirectUri { get; init; }

    /// <summary>
    /// SAML SP metadata URL — admin pastes this into the IdP's "App Federation Metadata URL"
    /// (EntraID) / "metadata import" (ADFS) field. <c>null</c> for non-SAML providers.
    /// </summary>
    public string? SamlSpMetadataUrl { get; init; }

    /// <summary>
    /// SAML Assertion Consumer Service URL — admin pastes this into the IdP's "Reply URL" /
    /// "AssertionConsumerService" field. <c>null</c> for non-SAML providers.
    /// </summary>
    public string? SamlAcsUrl { get; init; }
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
    /// <summary>
    /// Protocol family this flavor implements — <c>"Oidc"</c> or <c>"Saml"</c>.
    /// Admin UI uses this to dispatch the right tab layout (OIDC connection
    /// fields vs SAML metadata / attribute map). The flavor key alone is
    /// stable but the Type tells the UI which schema shape to render.
    /// </summary>
    public required string Type { get; init; }
}

public record FlavorConfigFieldDto(
    string Key,
    string Type,
    string Label,
    bool Required,
    string? HelpText,
    string? Placeholder);
