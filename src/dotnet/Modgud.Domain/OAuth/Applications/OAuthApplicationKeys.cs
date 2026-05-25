namespace Modgud.Domain.OAuth.Applications;

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

    // ─────── Dynamic Client Registration (RFC 7591) ────────
    // Set on creation by the /connect/register handler — admin-created
    // clients never carry these keys, which is how the rest of the
    // system (consent screen, resource-indicator handler, GC service)
    // distinguishes DCR clients from admin-registered ones.

    /// <summary>Boolean — <c>true</c> for clients minted via the public
    /// <c>/connect/register</c> endpoint. Single source of truth for
    /// "is this a DCR client".</summary>
    public const string DcrIsDynamicallyRegistered = "cocoar:dcr:is_dynamically_registered";

    /// <summary>ISO-8601 timestamp string of when the DCR registration
    /// happened. Stable for the lifetime of the client.</summary>
    public const string DcrRegisteredAt = "cocoar:dcr:registered_at";

    /// <summary>Source IP that submitted the registration request. Stored
    /// for audit-log correlation; not used for any policy decision after
    /// the registration completes.</summary>
    public const string DcrRegisteredFromIp = "cocoar:dcr:registered_from_ip";

    /// <summary>ISO-8601 timestamp string updated on each successful token
    /// issuance for this client. Drives the GC sweep — clients with
    /// <c>LastUsedAt</c> older than the per-realm DCR TTL get
    /// soft-deleted.</summary>
    public const string DcrLastUsedAt = "cocoar:dcr:last_used_at";
}
