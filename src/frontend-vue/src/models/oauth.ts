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
   * Optional FK to an Application. `null` = client is realm-wide (no app
   * context). The frontend joins this against `useApplicationsStore` to
   * resolve the slug + display name.
   */
  AppId?: string | null
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
  Enabled?: boolean
  RequireClientSecret?: boolean
  RequireConsent?: boolean
  AllowedGrantTypes?: string[]
  AllowedCorsOrigins?: string[]
  /** Application Id (Guid string). null/undefined → realm-wide client. */
  AppId?: string | null
}

export interface UpdateOAuthClientDto {
  DisplayName?: string | null
  ConsentType?: string | null
  RedirectUris?: string[] | null
  PostLogoutRedirectUris?: string[] | null
  Scopes?: string[] | null
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
   * App-link patch. Mirrors the backend's PATCH semantics:
   *   undefined/missing → no change (do NOT include the key in the JSON)
   *   ""                → explicit detach: AppId becomes null
   *   "<guid>"          → assign / change to that App
   * The dropdown's "no app" choice serialises to "" (empty string).
   */
  AppId?: string | null
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
  Secrets: ApiSecretEntryDto[]
}

export interface CreateOAuthApiDto {
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Enabled?: boolean
  Scopes?: string[]
  UserClaims?: string[]
}

export interface UpdateOAuthApiDto {
  DisplayName?: string | null
  Description?: string | null
  Enabled?: boolean | null
  Scopes?: string[] | null
  UserClaims?: string[] | null
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
