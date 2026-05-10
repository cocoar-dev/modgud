// OAuth admin models — mirror DTOs in
// src/dotnet-next/Cocoar.Auth.Application/DTOs/OAuth/.
// Backend serializes with PropertyNamingPolicy=null, so PascalCase is required.

export interface OAuthClientClaimDto {
  Type: string
  Value: string
}

export type AccessTokenType = 'Reference' | 'Jwt'
export type RefreshTokenUsage = 'OneTimeOnly' | 'ReUse'

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
  RefreshTokenUsage?: RefreshTokenUsage | string
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
  AbsoluteRefreshTokenLifetime?: number | null
  SlidingRefreshTokenLifetime?: number | null
  AlwaysSendClientClaims: boolean
  UpdateAccessTokenClaimsOnRefresh: boolean
  ClientClaimsPrefix?: string | null
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
  RefreshTokenUsage?: RefreshTokenUsage
  Enabled?: boolean
  RequireClientSecret?: boolean
  RequireConsent?: boolean
  AllowedGrantTypes?: string[]
  AllowedCorsOrigins?: string[]
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
  RefreshTokenUsage?: RefreshTokenUsage | null
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
  AbsoluteRefreshTokenLifetime?: number | null
  SlidingRefreshTokenLifetime?: number | null
  Claims?: OAuthClientClaimDto[] | null
  Roles?: string[] | null
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
}

// ─── APIs / Resources ──────────────────────────────────────────────────────

export interface ApiSecretEntryDto {
  SecretId: string
  Type: string
  Description?: string | null
  Expiration?: string | null
  CreatedAt: string
}

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
   * (RS exists but cannot authenticate against the distribution API yet).
   */
  AppId?: string | null
  /**
   * Subset of the linked App's permission catalog this RS gates on. Each
   * entry is an `AppPermission.Id` (Guid string) FK into `App.Permissions`.
   * Empty when the RS doesn't gate on anything yet.
   */
  PermissionIds: string[]
  Secrets: ApiSecretEntryDto[]
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
}

export interface OAuthApiListDto {
  Items: OAuthApiDto[]
  TotalCount: number
}

export interface OAuthApiCreatedDto extends Omit<OAuthApiDto, 'Secrets'> {
  ApiSecret: string
}

export interface CreateApiSecretDto {
  Type?: string
  Description?: string | null
  Expiration?: string | null
}

export interface ApiSecretCreatedDto {
  SecretId: string
  ApiSecret: string
}

export interface ApiSecretDto {
  ApiSecret: string
}
