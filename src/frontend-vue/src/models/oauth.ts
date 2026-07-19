// OAuth admin models — mirror DTOs in
// src/dotnet-next/Modgud.Application/DTOs/OAuth/.
// Backend serializes with PropertyNamingPolicy=null, so PascalCase is required.

export interface OAuthClientClaimDto {
  Type: string
  Value: string
}

export type AccessTokenType = 'Reference' | 'Jwt'

export interface OAuthClientDto {
  Id: string
  ClientId: string
  DisplayName?: string | null
  ClientType: string
  ConsentType: string
  RedirectUris: string[]
  PostLogoutRedirectUris: string[]
  Permissions: string[]
  AccessTokenType?: AccessTokenType | string
  CreatedAt?: string | null
  Enabled: boolean
  AllowAccessTokensViaBrowser: boolean
  RequireClientSecret: boolean
  EnableLocalLogin: boolean
  RequireConsent: boolean
  AllowRememberConsent: boolean
  AllowedGrantTypes: string[]
  AllowedCorsOrigins: string[]
  IdentityTokenLifetime?: number | null
  AccessTokenLifetime?: number | null
  AuthorizationCodeLifetime?: number | null
  SlidingRefreshTokenLifetime?: number | null
  AlwaysSendClientClaims: boolean
  UpdateAccessTokenClaimsOnRefresh: boolean
  ClientClaimsPrefix?: string | null
  /**
   * RFC 9126 — when `true`, this client MUST use Pushed Authorization
   * Requests: a direct (non-PAR) `/connect/authorize` request is rejected.
   * Off by default; PAR stays available to every client either way.
   */
  RequirePushedAuthorizationRequests: boolean
  /**
   * RFC 9449 (#118) — when `true`, this client MUST present a valid DPoP proof
   * at the token endpoint; a tokenless request is rejected. Off by default;
   * DPoP stays offered (bound on request) to every client either way.
   */
  RequireDpop: boolean
  /**
   * RFC 9449 §8-9 (#118) — when `true`, this client's DPoP proofs at the token
   * endpoint must carry a valid server-issued nonce (first proof gets a
   * `use_dpop_nonce` challenge + `DPoP-Nonce` header, then retries). Off by default.
   */
  RequireDpopNonce: boolean
  /**
   * ADR-0009 — admin-set per-client WebAuthn RP ID for native passkeys.
   * Null/blank = realm-scoped (the realm's primary domain). Changing it
   * invalidates all passkeys already enrolled for this client.
   */
  WebAuthnRpId?: string | null
  Claims: OAuthClientClaimDto[]
  Roles: string[]
  /**
   * Apps this client is linked to (Guid strings). Empty = realm-wide / no
   * app context. One id = typical SPA. Many = a frontend that bundles
   * multiple resource servers (Keycloak-style `resource_access` in the
   * issued tokens). Frontend joins these against `useApplicationsStore`
   * to resolve slugs and display names.
   */
  AppIds: string[]
  /**
   * `true` when this client was minted via the public /connect/register
   * endpoint (RFC 7591 Dynamic Client Registration). Drives the
   * "DCR"-badged row treatment in the admin grid and the optional
   * Registration-Info tab.
   */
  IsDynamicallyRegistered: boolean
  /** ISO-8601 timestamp of DCR registration. Null for non-DCR clients. */
  DcrRegisteredAt?: string | null
  /** Source IP that submitted the DCR registration. Null for non-DCR clients. */
  DcrRegisteredFromIp?: string | null
  /** ISO-8601 timestamp of the most recent token-issue for a DCR client. */
  DcrLastUsedAt?: string | null
  /**
   * ShortGuid of the ServiceAccount that owns this client's credentials, or
   * null for user-flow clients. Drives the M2M-badge in the admin grid and
   * the read-only modal that deep-links to the SA editor.
   */
  LinkedServiceAccountId?: string | null
}

export interface CreateOAuthClientDto {
  ClientId: string
  DisplayName?: string | null
  ClientType: string
  ClientSecret?: string | null
  ConsentType?: string
  RedirectUris?: string[]
  PostLogoutRedirectUris?: string[]
  Scopes?: string[]
  AccessTokenType?: AccessTokenType
  Enabled?: boolean
  RequireClientSecret?: boolean
  RequireConsent?: boolean
  AllowedGrantTypes?: string[]
  AllowedCorsOrigins?: string[]
  /** RFC 9126 — require this client to use Pushed Authorization Requests. Off by default. */
  RequirePushedAuthorizationRequests?: boolean
  /** RFC 9449 (#118) — require this client to present a DPoP proof at the token endpoint. Off by default. */
  RequireDpop?: boolean
  /** RFC 9449 §8-9 (#118) — require this client's DPoP proofs to carry a server-issued nonce. Off by default. */
  RequireDpopNonce?: boolean
  /** ADR-0009 — admin-set per-client WebAuthn RP ID. Blank = realm-scoped. */
  WebAuthnRpId?: string | null
  /**
   * Apps this client links to. Empty/undefined = realm-wide. Multiple
   * entries = Keycloak-style multi-app client.
   */
  AppIds?: string[]
}

export interface UpdateOAuthClientDto {
  DisplayName?: string | null
  ConsentType?: string | null
  RedirectUris?: string[] | null
  PostLogoutRedirectUris?: string[] | null
  Scopes?: string[] | null
  AccessTokenType?: AccessTokenType | null
  Enabled?: boolean | null
  AllowAccessTokensViaBrowser?: boolean | null
  RequireClientSecret?: boolean | null
  EnableLocalLogin?: boolean | null
  RequireConsent?: boolean | null
  AllowRememberConsent?: boolean | null
  AllowedGrantTypes?: string[] | null
  AllowedCorsOrigins?: string[] | null
  IdentityTokenLifetime?: number | null
  AccessTokenLifetime?: number | null
  AuthorizationCodeLifetime?: number | null
  SlidingRefreshTokenLifetime?: number | null
  Claims?: OAuthClientClaimDto[] | null
  Roles?: string[] | null
  /** RFC 9126 PAR-requirement patch: null/missing = no change, true/false sets it. */
  RequirePushedAuthorizationRequests?: boolean | null
  /** RFC 9449 (#118) DPoP-requirement patch: null/missing = no change, true/false sets it. */
  RequireDpop?: boolean | null
  /** RFC 9449 §8-9 (#118) DPoP-nonce-requirement patch: null/missing = no change, true/false sets it. */
  RequireDpopNonce?: boolean | null
  /**
   * ADR-0009 per-client WebAuthn RP ID patch:
   *   undefined/missing → no change
   *   "" (empty)        → clear back to realm-scoped
   *   "host"            → set
   */
  WebAuthnRpId?: string | null
  /**
   * App-link patch (set semantics, mirrors the backend):
   *   undefined/missing → no change
   *   []                → explicit detach-all (realm-wide)
   *   [a, b, …]         → replace the full list
   * The MultiSelect's selected values are sent verbatim on save.
   */
  AppIds?: string[] | null
}

export interface OAuthClientListDto {
  Items: OAuthClientDto[]
  TotalCount: number
}

export interface OAuthClientCreatedDto {
  Client: OAuthClientDto
  ClientSecret?: string | null
}

export interface ClientSecretDto {
  ClientSecret: string
}

// ─── Scopes ────────────────────────────────────────────────────────────────

export interface OAuthScopeDto {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Resources: string[]
  Enabled: boolean
  Required: boolean
  Emphasize: boolean
  ShowInDiscoveryDocument: boolean
  UserClaims: string[]
  /**
   * Optional FK to an Application. `null` = global scope (cross-app, e.g.
   * the standard OIDC scopes openid/email/profile/roles/offline_access).
   * App-scoped scopes can only be requested by clients linked to the
   * same App.
   */
  AppId?: string | null
  /**
   * True for the five OIDC standard scopes (openid/email/profile/roles/
   * offline_access) — shipped with the IdP and not editable. Drives the
   * dimmed row treatment in the admin grid.
   */
  IsStandard: boolean
  /**
   * Per-scope opt-in for Dynamic Client Registration (RFC 7591). When
   * `true`, clients minted via DCR can request this scope; default `false`.
   * Capability-containment half of the triple-opt-in (master toggle +
   * per-Api flag + per-Scope flag).
   */
  AllowDynamicRegistrationClients: boolean
}

export interface CreateOAuthScopeDto {
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Resources?: string[]
  Enabled?: boolean
  Required?: boolean
  Emphasize?: boolean
  ShowInDiscoveryDocument?: boolean
  UserClaims?: string[]
  /** App.Id (Guid string). Null/undefined = global scope. */
  AppId?: string | null
  /** Per-scope DCR opt-in. Default `false`. */
  AllowDynamicRegistrationClients?: boolean
}

export interface OAuthScopeListDto {
  Items: OAuthScopeDto[]
  TotalCount: number
}

export interface UpdateOAuthScopeDto {
  DisplayName?: string | null
  Description?: string | null
  Resources?: string[] | null
  Enabled?: boolean | null
  Required?: boolean | null
  Emphasize?: boolean | null
  ShowInDiscoveryDocument?: boolean | null
  UserClaims?: string[] | null
  /** PATCH semantics: undefined/missing = no change, "" = make global, "<guid>" = assign. */
  AppId?: string | null
  /** PATCH semantics: null = no change. */
  AllowDynamicRegistrationClients?: boolean | null
}

// ─── APIs / Resources ──────────────────────────────────────────────────────

export interface OAuthApiDto {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Enabled: boolean
  Scopes: string[]
  UserClaims: string[]
  /**
   * App.Id (Guid string) the resource server belongs to. Null = unassigned
   * (the IdP has no catalog for this RS and won't emit a per-Audience
   * `resource_access` block on UserInfo).
   */
  AppId?: string | null
  /**
   * Subset of the linked App's permission catalog this RS gates on. Each
   * entry is an `AppPermission.Id` (Guid string) FK into `App.Permissions`.
   * Empty when the RS doesn't gate on anything yet.
   */
  PermissionIds: string[]
  /**
   * `true` when a sibling `OAuthScope` with the same `Name` already exists.
   * Drives the admin UI "Create implicit scope" affordance — hidden when
   * the API already has its 1:1 scope wired up.
   */
  HasImplicitScope: boolean
  /**
   * Per-API opt-in for Dynamic Client Registration (RFC 7591). When
   * `true`, DCR-registered clients can target this RS via `resource=`.
   * Resource-target-containment half of the triple-opt-in.
   */
  AllowDynamicRegistration: boolean
}

export interface CreateOAuthApiDto {
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Enabled?: boolean
  Scopes?: string[]
  UserClaims?: string[]
  /** App.Id (Guid string). Null/undefined = unassigned. */
  AppId?: string | null
  /**
   * Optional initial subset of the linked App's catalog. Each entry is an
   * `AppPermission.Id` (Guid string). Validated against the linked App's
   * catalog at create time. Must be empty/absent when AppId is null.
   */
  PermissionIds?: string[]
  /** Per-API DCR opt-in. Default `false`. */
  AllowDynamicRegistration?: boolean
}

export interface UpdateOAuthApiDto {
  DisplayName?: string | null
  Description?: string | null
  Enabled?: boolean | null
  Scopes?: string[] | null
  UserClaims?: string[] | null
  /**
   * PATCH semantics: undefined/missing = no change, "" = detach,
   * "<guid>" = assign / change. The dropdown's "unassigned" choice
   * serialises to "" (empty string).
   */
  AppId?: string | null
  /**
   * PATCH semantics: undefined/missing = no change, [] = clear,
   * [...] = replace. Validated against the linked App's catalog.
   */
  PermissionIds?: string[] | null
  /** PATCH semantics: null = no change. */
  AllowDynamicRegistration?: boolean | null
}

export interface OAuthApiListDto {
  Items: OAuthApiDto[]
  TotalCount: number
}

export interface OAuthApiCreatedDto {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Enabled: boolean
  Scopes: string[]
  UserClaims: string[]
}
