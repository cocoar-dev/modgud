namespace Cocoar.Auth.Domain.OAuth.Applications;

/// <summary>Custom OAuth application setting keys (simple string values).</summary>
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

/// <summary>Custom OAuth application property keys (JSON-element values for complex types).</summary>
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
