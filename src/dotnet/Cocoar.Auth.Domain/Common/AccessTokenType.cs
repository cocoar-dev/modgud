namespace Cocoar.Auth.Domain.Common;

/// <summary>
/// Specifies the type of access token issued to a client.
/// </summary>
public enum AccessTokenType
{
	/// <summary>
	/// Reference token - an opaque identifier is returned to the client.
	/// The actual token payload is stored server-side and must be resolved via introspection.
	/// This is the default and recommended option for maximum security and revocability.
	/// </summary>
	Reference = 0,

	/// <summary>
	/// JWT (JSON Web Token) - a self-contained token is returned to the client.
	/// The token contains all claims and can be validated without contacting the server.
	/// </summary>
	Jwt = 1
}

/// <summary>
/// Specifies how refresh tokens are handled.
/// </summary>
public enum RefreshTokenUsage
{
	/// <summary>
	/// The refresh token handle will be updated when refreshing tokens.
	/// This is the default and recommended option (rotation).
	/// </summary>
	OneTimeOnly = 0,

	/// <summary>
	/// The refresh token handle will stay the same when refreshing tokens.
	/// </summary>
	ReUse = 1
}

/// <summary>
/// Represents a claim associated with an OAuth client.
/// </summary>
public record OAuthClientClaim(string Type, string Value);

/// <summary>
/// Constants for custom OAuth application settings keys.
/// Stored in the OpenIddict Settings dictionary (simple string values).
/// </summary>
public static class OAuthApplicationSettingKeys
{
	public const string AccessTokenType = "cocoar:access_token_type";
	public const string RefreshTokenUsage = "cocoar:refresh_token_usage";
	public const string IdentityTokenLifetime = "cocoar:identity_token_lifetime";
	public const string AccessTokenLifetime = "cocoar:access_token_lifetime";
	public const string AuthorizationCodeLifetime = "cocoar:authorization_code_lifetime";
	public const string AbsoluteRefreshTokenLifetime = "cocoar:absolute_refresh_token_lifetime";
	public const string SlidingRefreshTokenLifetime = "cocoar:sliding_refresh_token_lifetime";
	public const string ClientClaimsPrefix = "cocoar:client_claims_prefix";
}

/// <summary>
/// Constants for custom OAuth application property keys.
/// Stored in the OpenIddict Properties dictionary (JSON elements for complex values).
/// </summary>
public static class OAuthApplicationPropertyKeys
{
	public const string Enabled = "cocoar:enabled";
	public const string AllowAccessTokensViaBrowser = "cocoar:allow_access_tokens_via_browser";
	public const string RequireClientSecret = "cocoar:require_client_secret";
	public const string EnableLocalLogin = "cocoar:enable_local_login";
	public const string RequireConsent = "cocoar:require_consent";
	public const string AllowRememberConsent = "cocoar:allow_remember_consent";
	public const string AllowedCorsOrigins = "cocoar:allowed_cors_origins";
	public const string AlwaysSendClientClaims = "cocoar:always_send_client_claims";
	public const string UpdateAccessTokenClaimsOnRefresh = "cocoar:update_access_token_claims_on_refresh";
	public const string ClientClaims = "cocoar:client_claims";
	public const string Roles = "cocoar:roles";
}
