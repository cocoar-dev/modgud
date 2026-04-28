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
