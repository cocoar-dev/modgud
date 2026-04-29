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
    /// Optional FK to <c>App.Id</c> the client belongs to (Guid as string).
    /// <c>null</c> means the client is realm-wide (no app context). The
    /// frontend joins this against its apps store to resolve the slug.
    /// </summary>
    public string? AppId { get; init; }
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

    /// <summary>App this client belongs to (Id, ShortGuid string). Null = unassigned.</summary>
    public string? AppId { get; init; }
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
    /// App-link patch. Mirrors the rest of this PATCH-style DTO:
    /// <list type="bullet">
    ///   <item><c>null</c> — field omitted, no change to the stored AppId.</item>
    ///   <item><c>""</c> (empty string) — explicit detach: AppId becomes <c>null</c>.</item>
    ///   <item>any non-empty Guid string — assign or change to that App.</item>
    /// </list>
    /// The Vue admin dropdown serialises its "no app" choice as the empty
    /// string and an actual selection as the App's Id (ShortGuid).
    /// </summary>
    public string? AppId { get; init; }
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
