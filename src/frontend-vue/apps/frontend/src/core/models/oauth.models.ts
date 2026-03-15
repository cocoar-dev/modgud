export interface OAuthClientClaim {
  type: string;
  value: string;
}

export interface ClientSecretEntry {
  type: string;
  value: string;
  expiration?: string;
  description?: string;
}

export interface OAuthClient {
  id: string;
  clientId: string;
  displayName?: string;
  clientType: 'public' | 'confidential';
  consentType: 'explicit' | 'implicit' | 'external';
  redirectUris: string[];
  postLogoutRedirectUris: string[];
  permissions: string[];
  createdAt?: string;

  enabled?: boolean;
  accessTokenType?: 'Reference' | 'Jwt';
  refreshTokenUsage?: 'OneTimeOnly' | 'ReUse';
  allowAccessTokensViaBrowser?: boolean;
  requireClientSecret?: boolean;
  enableLocalLogin?: boolean;
  requireConsent?: boolean;
  allowRememberConsent?: boolean;
  allowedGrantTypes?: string[];
  allowedCorsOrigins?: string[];
  identityTokenLifetime?: number;
  accessTokenLifetime?: number;
  authorizationCodeLifetime?: number;
  absoluteRefreshTokenLifetime?: number;
  slidingRefreshTokenLifetime?: number;
  roles?: string[];
  alwaysSendClientClaims?: boolean;
  updateAccessTokenClaimsOnRefresh?: boolean;
  clientClaimsPrefix?: string;
  claims?: OAuthClientClaim[];
  clientSecrets?: ClientSecretEntry[];
}

export interface CreateOAuthClientRequest {
  clientId: string;
  displayName?: string;
  clientType: 'public' | 'confidential';
  clientSecret?: string;
  consentType?: 'explicit' | 'implicit' | 'external';
  redirectUris?: string[];
  postLogoutRedirectUris?: string[];
  scopes?: string[];

  enabled?: boolean;
  accessTokenType?: 'Reference' | 'Jwt';
  refreshTokenUsage?: 'OneTimeOnly' | 'ReUse';
  allowAccessTokensViaBrowser?: boolean;
  requireClientSecret?: boolean;
  enableLocalLogin?: boolean;
  requireConsent?: boolean;
  allowRememberConsent?: boolean;
  allowedGrantTypes?: string[];
  allowedCorsOrigins?: string[];
  identityTokenLifetime?: number;
  accessTokenLifetime?: number;
  authorizationCodeLifetime?: number;
  absoluteRefreshTokenLifetime?: number;
  slidingRefreshTokenLifetime?: number;
  roles?: string[];
  alwaysSendClientClaims?: boolean;
  updateAccessTokenClaimsOnRefresh?: boolean;
  clientClaimsPrefix?: string;
  claims?: OAuthClientClaim[];
}

export interface UpdateOAuthClientRequest {
  displayName?: string;
  consentType?: 'explicit' | 'implicit' | 'external';
  redirectUris?: string[];
  postLogoutRedirectUris?: string[];
  scopes?: string[];

  enabled?: boolean;
  accessTokenType?: 'Reference' | 'Jwt';
  refreshTokenUsage?: 'OneTimeOnly' | 'ReUse';
  allowAccessTokensViaBrowser?: boolean;
  requireClientSecret?: boolean;
  enableLocalLogin?: boolean;
  requireConsent?: boolean;
  allowRememberConsent?: boolean;
  allowedGrantTypes?: string[];
  allowedCorsOrigins?: string[];
  identityTokenLifetime?: number;
  accessTokenLifetime?: number;
  authorizationCodeLifetime?: number;
  absoluteRefreshTokenLifetime?: number;
  slidingRefreshTokenLifetime?: number;
  roles?: string[];
  alwaysSendClientClaims?: boolean;
  updateAccessTokenClaimsOnRefresh?: boolean;
  clientClaimsPrefix?: string;
  claims?: OAuthClientClaim[];
}

export interface OAuthClientList {
  items: OAuthClient[];
  totalCount: number;
}

export interface OAuthClientCreated {
  client: OAuthClient;
  clientSecret?: string;
}

export interface ClientSecret {
  clientSecret: string;
}

export interface OAuthScope {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  resources: string[];
  enabled: boolean;
  required: boolean;
  emphasize: boolean;
  showInDiscoveryDocument: boolean;
  userClaims: string[];
}

export interface CreateOAuthScopeRequest {
  name: string;
  displayName?: string;
  description?: string;
  resources?: string[];
  enabled?: boolean;
  required?: boolean;
  emphasize?: boolean;
  showInDiscoveryDocument?: boolean;
  userClaims?: string[];
}

export interface UpdateOAuthScopeRequest {
  displayName?: string;
  description?: string;
  resources?: string[];
  enabled?: boolean;
  required?: boolean;
  emphasize?: boolean;
  showInDiscoveryDocument?: boolean;
  userClaims?: string[];
}

export interface OAuthScopeList {
  items: OAuthScope[];
  totalCount: number;
}

export interface ApiSecretEntry {
  secretId: string;
  type: string;
  description?: string;
  expiration?: string;
  createdAt: string;
}

export interface OAuthApiResource {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  enabled: boolean;
  scopes: string[];
  userClaims: string[];
  secrets: ApiSecretEntry[];
}

export interface CreateOAuthApiResourceRequest {
  name: string;
  displayName?: string;
  description?: string;
  enabled?: boolean;
  scopes?: string[];
  userClaims?: string[];
}

export interface UpdateOAuthApiResourceRequest {
  displayName?: string;
  description?: string;
  enabled?: boolean;
  scopes?: string[];
  userClaims?: string[];
}

export interface OAuthApiResourceList {
  items: OAuthApiResource[];
  totalCount: number;
}

export interface OAuthApiResourceCreated extends OAuthApiResource {
  apiSecret: string;
}

export interface ApiSecret {
  apiSecret: string;
}

export interface CreateApiSecretRequest {
  type?: string;
  description?: string;
  expiration?: string;
}

export interface ApiSecretCreated {
  secretId: string;
  apiSecret: string;
}
