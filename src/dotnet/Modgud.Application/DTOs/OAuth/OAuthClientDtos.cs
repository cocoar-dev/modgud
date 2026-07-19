using Modgud.Domain.OAuth.Common;

namespace Modgud.Application.DTOs.OAuth;

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
    public int? SlidingRefreshTokenLifetime { get; init; }

    public bool AlwaysSendClientClaims { get; init; }
    public bool UpdateAccessTokenClaimsOnRefresh { get; init; }
    public string? ClientClaimsPrefix { get; init; }
    public List<OAuthClientClaimDto> Claims { get; init; } = [];

    /// <summary>
    /// RFC 9126 — when <c>true</c>, this client MUST use Pushed Authorization
    /// Requests: a direct (non-PAR) <c>/connect/authorize</c> request is
    /// rejected. Off by default; PAR stays available to every client either way.
    /// </summary>
    public bool RequirePushedAuthorizationRequests { get; init; }

    /// <summary>
    /// RFC 9449 (#118) — when <c>true</c>, this client MUST present a valid DPoP
    /// proof at the token endpoint; a tokenless request is rejected. Off by
    /// default; DPoP stays offered (bound on request) to every client either way.
    /// </summary>
    public bool RequireDpop { get; init; }

    /// <summary>
    /// ADR-0009 — admin-set per-client WebAuthn RP ID for native passkeys. Null/blank
    /// ⇒ realm-scoped (the realm's PrimaryDomain). Changing it invalidates all passkeys
    /// already enrolled for this client.
    /// </summary>
    public string? WebAuthnRpId { get; init; }

    public List<string> Roles { get; init; } = [];

    /// <summary>
    /// Apps this client is linked to (Guid strings). Empty = realm-wide /
    /// unassigned. One id = typical SPA. Many = a frontend that bundles
    /// multiple resource servers (Keycloak-style <c>resource_access</c> in
    /// the issued token's UserInfo claims). The frontend joins these
    /// against its apps store to resolve slugs.
    /// </summary>
    public List<string> AppIds { get; init; } = [];

    /// <summary>
    /// <c>true</c> when this client was minted via the public
    /// <c>/connect/register</c> endpoint (RFC 7591). Drives the
    /// "DCR"-badged row treatment in the admin grid and the optional
    /// Registration-Info tab. Admin-created clients always carry
    /// <c>false</c>; the field doesn't appear in the Update DTO since
    /// admins can't retroactively "convert" a client into a DCR one.
    /// </summary>
    public bool IsDynamicallyRegistered { get; init; }

    /// <summary>ISO-8601 timestamp of when DCR registration happened.
    /// Null for non-DCR clients.</summary>
    public DateTimeOffset? DcrRegisteredAt { get; init; }

    /// <summary>Source IP that submitted the DCR registration. Null for
    /// non-DCR clients.</summary>
    public string? DcrRegisteredFromIp { get; init; }

    /// <summary>ISO-8601 timestamp updated by the GC infra on each
    /// successful token-issue for DCR clients. Drives the soft-delete
    /// sweep. Null for non-DCR clients.</summary>
    public DateTimeOffset? DcrLastUsedAt { get; init; }

    /// <summary>
    /// ShortGuid of the ServiceAccount that owns this client's credentials,
    /// or null for user-flow clients. Drives the M2M-badge in the Admin
    /// grid and the read-only modal that deep-links into the SA editor.
    /// Strict separation: one OAuth client = one auth mode (linked +
    /// <c>client_credentials</c>, or unlinked + user-flow grants).
    /// </summary>
    public string? LinkedServiceAccountId { get; init; }
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
    public int? SlidingRefreshTokenLifetime { get; init; }

    public bool AlwaysSendClientClaims { get; init; }
    public bool UpdateAccessTokenClaimsOnRefresh { get; init; }
    public string? ClientClaimsPrefix { get; init; }
    public List<OAuthClientClaimDto> Claims { get; init; } = [];

    /// <summary>RFC 9126 — when <c>true</c>, this client must use Pushed
    /// Authorization Requests; a direct authorize request is rejected. Off by
    /// default.</summary>
    public bool RequirePushedAuthorizationRequests { get; init; }

    /// <summary>RFC 9449 (#118) — when <c>true</c>, this client must present a
    /// valid DPoP proof at the token endpoint. Off by default.</summary>
    public bool RequireDpop { get; init; }

    /// <summary>ADR-0009 — admin-set per-client WebAuthn RP ID for native passkeys.
    /// Null/blank ⇒ realm-scoped (the realm's PrimaryDomain).</summary>
    public string? WebAuthnRpId { get; init; }

    public List<string> Roles { get; init; } = [];

    /// <summary>
    /// Apps this client belongs to (Guid strings). Empty/null = realm-wide.
    /// </summary>
    public List<string>? AppIds { get; init; }

    /// <summary>
    /// Optional ShortGuid of a ServiceAccount that should own this client.
    /// Required when <see cref="AllowedGrantTypes"/> includes
    /// <c>client_credentials</c>, and forbidden when any user-flow grant
    /// is present — endpoint-level validation enforces the split.
    /// </summary>
    public string? LinkedServiceAccountId { get; init; }
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
    public int? SlidingRefreshTokenLifetime { get; init; }

    public bool? AlwaysSendClientClaims { get; init; }
    public bool? UpdateAccessTokenClaimsOnRefresh { get; init; }
    public string? ClientClaimsPrefix { get; init; }
    public List<OAuthClientClaimDto>? Claims { get; init; }

    /// <summary>RFC 9126 PAR requirement patch. <c>null</c> = field omitted (no
    /// change); <c>true</c>/<c>false</c> sets it.</summary>
    public bool? RequirePushedAuthorizationRequests { get; init; }

    /// <summary>RFC 9449 (#118) DPoP requirement patch. <c>null</c> = field omitted
    /// (no change); <c>true</c>/<c>false</c> sets it.</summary>
    public bool? RequireDpop { get; init; }

    /// <summary>
    /// ADR-0009 per-client WebAuthn RP ID patch. <c>null</c> = field omitted (no
    /// change); empty string = clear back to realm-scoped; any non-blank value sets it.
    /// </summary>
    public string? WebAuthnRpId { get; init; }

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
