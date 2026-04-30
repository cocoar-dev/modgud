using Cocoar.Auth.Domain.OAuth.Common;

namespace Cocoar.Auth.Application.DTOs.OAuth;

public record OAuthClientDto
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public string? DisplayName { get; init; }
    public required string ClientType { get; init; }
    public required string ConsentType { get; init; }
    public required List<string> RedirectUris { get; init; }
    public required List<string> PostLogoutRedirectUris { get; init; }
    public required List<string> Permissions { get; init; }
    public AccessTokenType AccessTokenType { get; init; } = AccessTokenType.Reference;
    public DateTimeOffset? CreatedAt { get; init; }

    public bool Enabled { get; init; } = true;
    public RefreshTokenUsage RefreshTokenUsage { get; init; } = RefreshTokenUsage.OneTimeOnly;
    public bool AllowAccessTokensViaBrowser { get; init; }
    public bool RequireClientSecret { get; init; } = true;
    public bool EnableLocalLogin { get; init; } = true;
    public bool RequireConsent { get; init; }
    public bool AllowRememberConsent { get; init; } = true;
    public List<string> AllowedGrantTypes { get; init; } = [];
    public List<string> AllowedCorsOrigins { get; init; } = [];

    public int? IdentityTokenLifetime { get; init; }
    public int? AccessTokenLifetime { get; init; }
    public int? AuthorizationCodeLifetime { get; init; }
    public int? AbsoluteRefreshTokenLifetime { get; init; }
    public int? SlidingRefreshTokenLifetime { get; init; }

    public bool AlwaysSendClientClaims { get; init; }
    public bool UpdateAccessTokenClaimsOnRefresh { get; init; }
    public string? ClientClaimsPrefix { get; init; }
    public List<OAuthClientClaimDto> Claims { get; init; } = [];

    public List<string> Roles { get; init; } = [];

    /// <summary>
    /// Apps this client is linked to (Guid strings). Empty = realm-wide /
    /// unassigned. One id = typical SPA. Many = a frontend that bundles
    /// multiple resource servers (Keycloak-style <c>resource_access</c> in
    /// the issued token's UserInfo claims). The frontend joins these
    /// against its apps store to resolve slugs.
    /// </summary>
    public List<string> AppIds { get; init; } = [];
}

public record OAuthClientClaimDto
{
    public required string Type { get; init; }
    public required string Value { get; init; }
}

public record CreateOAuthClientDto
{
    public required string ClientId { get; init; }
    public string? DisplayName { get; init; }
    public required string ClientType { get; init; }
    public string? ClientSecret { get; init; }
    public string ConsentType { get; init; } = "implicit";
    public List<string> RedirectUris { get; init; } = [];
    public List<string> PostLogoutRedirectUris { get; init; } = [];
    public List<string> Scopes { get; init; } = [];

    public AccessTokenType AccessTokenType { get; init; } = AccessTokenType.Reference;

    public bool Enabled { get; init; } = true;
    public RefreshTokenUsage RefreshTokenUsage { get; init; } = RefreshTokenUsage.OneTimeOnly;
    public bool AllowAccessTokensViaBrowser { get; init; }
    public bool RequireClientSecret { get; init; } = true;
    public bool EnableLocalLogin { get; init; } = true;
    public bool RequireConsent { get; init; }
    public bool AllowRememberConsent { get; init; } = true;
    public List<string> AllowedGrantTypes { get; init; } = [];
    public List<string> AllowedCorsOrigins { get; init; } = [];

    public int? IdentityTokenLifetime { get; init; }
    public int? AccessTokenLifetime { get; init; }
    public int? AuthorizationCodeLifetime { get; init; }
    public int? AbsoluteRefreshTokenLifetime { get; init; }
    public int? SlidingRefreshTokenLifetime { get; init; }

    public bool AlwaysSendClientClaims { get; init; }
    public bool UpdateAccessTokenClaimsOnRefresh { get; init; }
    public string? ClientClaimsPrefix { get; init; }
    public List<OAuthClientClaimDto> Claims { get; init; } = [];

    public List<string> Roles { get; init; } = [];

    /// <summary>
    /// Apps this client belongs to (Guid strings). Empty/null = realm-wide.
    /// </summary>
    public List<string>? AppIds { get; init; }
}

public record UpdateOAuthClientDto
{
    public string? DisplayName { get; init; }
    public string? ConsentType { get; init; }
    public List<string>? RedirectUris { get; init; }
    public List<string>? PostLogoutRedirectUris { get; init; }
    public List<string>? Scopes { get; init; }

    public AccessTokenType? AccessTokenType { get; init; }

    public bool? Enabled { get; init; }
    public RefreshTokenUsage? RefreshTokenUsage { get; init; }
    public bool? AllowAccessTokensViaBrowser { get; init; }
    public bool? RequireClientSecret { get; init; }
    public bool? EnableLocalLogin { get; init; }
    public bool? RequireConsent { get; init; }
    public bool? AllowRememberConsent { get; init; }
    public List<string>? AllowedGrantTypes { get; init; }
    public List<string>? AllowedCorsOrigins { get; init; }

    public int? IdentityTokenLifetime { get; init; }
    public int? AccessTokenLifetime { get; init; }
    public int? AuthorizationCodeLifetime { get; init; }
    public int? AbsoluteRefreshTokenLifetime { get; init; }
    public int? SlidingRefreshTokenLifetime { get; init; }

    public bool? AlwaysSendClientClaims { get; init; }
    public bool? UpdateAccessTokenClaimsOnRefresh { get; init; }
    public string? ClientClaimsPrefix { get; init; }
    public List<OAuthClientClaimDto>? Claims { get; init; }

    public List<string>? Roles { get; init; }

    /// <summary>
    /// App-link patch. <c>null</c> = field omitted, no change to the stored
    /// list. An empty array <c>[]</c> = explicit detach-all (realm-wide).
    /// Any non-empty array replaces the full list (set semantics, not
    /// merge). The Vue admin always sends the dropdown's current selection
    /// on save.
    /// </summary>
    public List<string>? AppIds { get; init; }
}

public record OAuthClientListDto
{
    public required List<OAuthClientDto> Items { get; init; }
    public int TotalCount { get; init; }
}

public record ClientSecretDto
{
    public required string ClientSecret { get; init; }
}

public record OAuthClientCreatedDto
{
    public required OAuthClientDto Client { get; init; }
    public string? ClientSecret { get; init; }
}
