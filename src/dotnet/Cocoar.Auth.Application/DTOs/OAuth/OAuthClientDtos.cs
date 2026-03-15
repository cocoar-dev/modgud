using Cocoar.Auth.Domain.Common;

namespace Cocoar.Auth.Application.DTOs.OAuth;

/// <summary>
/// OAuth client information for API responses.
/// </summary>
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

	// Extended client properties
	public bool Enabled { get; init; } = true;
	public RefreshTokenUsage RefreshTokenUsage { get; init; } = RefreshTokenUsage.OneTimeOnly;
	public bool AllowAccessTokensViaBrowser { get; init; }
	public bool RequireClientSecret { get; init; } = true;
	public bool EnableLocalLogin { get; init; } = true;
	public bool RequireConsent { get; init; }
	public bool AllowRememberConsent { get; init; } = true;
	public List<string> AllowedGrantTypes { get; init; } = [];
	public List<string> AllowedCorsOrigins { get; init; } = [];

	// Token lifetime options
	public int? IdentityTokenLifetime { get; init; }
	public int? AccessTokenLifetime { get; init; }
	public int? AuthorizationCodeLifetime { get; init; }
	public int? AbsoluteRefreshTokenLifetime { get; init; }
	public int? SlidingRefreshTokenLifetime { get; init; }

	// Client claims
	public bool AlwaysSendClientClaims { get; init; }
	public bool UpdateAccessTokenClaimsOnRefresh { get; init; }
	public string? ClientClaimsPrefix { get; init; }
	public List<OAuthClientClaimDto> Claims { get; init; } = [];

	// Client roles
	public List<string> Roles { get; init; } = [];
}

/// <summary>
/// Represents a claim associated with an OAuth client.
/// </summary>
public record OAuthClientClaimDto
{
	public required string Type { get; init; }
	public required string Value { get; init; }
}

/// <summary>
/// Request to create a new OAuth client.
/// </summary>
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

	/// <summary>
	/// The type of access token to issue for this client.
	/// Defaults to Reference (opaque, server-side stored) for maximum security.
	/// </summary>
	public AccessTokenType AccessTokenType { get; init; } = AccessTokenType.Reference;

	// Extended client properties
	public bool Enabled { get; init; } = true;
	public RefreshTokenUsage RefreshTokenUsage { get; init; } = RefreshTokenUsage.OneTimeOnly;
	public bool AllowAccessTokensViaBrowser { get; init; }
	public bool RequireClientSecret { get; init; } = true;
	public bool EnableLocalLogin { get; init; } = true;
	public bool RequireConsent { get; init; }
	public bool AllowRememberConsent { get; init; } = true;
	public List<string> AllowedGrantTypes { get; init; } = [];
	public List<string> AllowedCorsOrigins { get; init; } = [];

	// Token lifetime options (null = use server defaults)
	public int? IdentityTokenLifetime { get; init; }
	public int? AccessTokenLifetime { get; init; }
	public int? AuthorizationCodeLifetime { get; init; }
	public int? AbsoluteRefreshTokenLifetime { get; init; }
	public int? SlidingRefreshTokenLifetime { get; init; }

	// Client claims
	public bool AlwaysSendClientClaims { get; init; }
	public bool UpdateAccessTokenClaimsOnRefresh { get; init; }
	public string? ClientClaimsPrefix { get; init; }
	public List<OAuthClientClaimDto> Claims { get; init; } = [];

	// Client roles
	public List<string> Roles { get; init; } = [];
}

/// <summary>
/// Request to update an existing OAuth client.
/// </summary>
public record UpdateOAuthClientDto
{
	public string? DisplayName { get; init; }
	public string? ConsentType { get; init; }
	public List<string>? RedirectUris { get; init; }
	public List<string>? PostLogoutRedirectUris { get; init; }
	public List<string>? Scopes { get; init; }

	/// <summary>
	/// The type of access token to issue for this client.
	/// Null means no change.
	/// </summary>
	public AccessTokenType? AccessTokenType { get; init; }

	// Extended client properties (null = no change)
	public bool? Enabled { get; init; }
	public RefreshTokenUsage? RefreshTokenUsage { get; init; }
	public bool? AllowAccessTokensViaBrowser { get; init; }
	public bool? RequireClientSecret { get; init; }
	public bool? EnableLocalLogin { get; init; }
	public bool? RequireConsent { get; init; }
	public bool? AllowRememberConsent { get; init; }
	public List<string>? AllowedGrantTypes { get; init; }
	public List<string>? AllowedCorsOrigins { get; init; }

	// Token lifetime options (null = no change)
	public int? IdentityTokenLifetime { get; init; }
	public int? AccessTokenLifetime { get; init; }
	public int? AuthorizationCodeLifetime { get; init; }
	public int? AbsoluteRefreshTokenLifetime { get; init; }
	public int? SlidingRefreshTokenLifetime { get; init; }

	// Client claims (null = no change)
	public bool? AlwaysSendClientClaims { get; init; }
	public bool? UpdateAccessTokenClaimsOnRefresh { get; init; }
	public string? ClientClaimsPrefix { get; init; }
	public List<OAuthClientClaimDto>? Claims { get; init; }

	// Client roles (null = no change)
	public List<string>? Roles { get; init; }
}

/// <summary>
/// Response containing a list of OAuth clients.
/// </summary>
public record OAuthClientListDto
{
	public required List<OAuthClientDto> Items { get; init; }
	public int TotalCount { get; init; }
}

/// <summary>
/// Response containing a newly generated client secret.
/// </summary>
public record ClientSecretDto
{
	public required string ClientSecret { get; init; }
}

/// <summary>
/// Response containing the created client with its secret.
/// </summary>
public record OAuthClientCreatedDto
{
	public required OAuthClientDto Client { get; init; }
	public string? ClientSecret { get; init; }
}
