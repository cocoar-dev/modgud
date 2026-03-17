// ============================================================================
// OAuth Client DTOs
// ============================================================================

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
}

export interface UpdateOAuthClientRequest {
  displayName?: string;
  consentType?: 'explicit' | 'implicit' | 'external';
  redirectUris?: string[];
  postLogoutRedirectUris?: string[];
  scopes?: string[];
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

// ============================================================================
// OAuth Scope DTOs
// ============================================================================

export interface OAuthScope {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  resources: string[];
}

export interface CreateOAuthScopeRequest {
  name: string;
  displayName?: string;
  description?: string;
  resources?: string[];
}

export interface UpdateOAuthScopeRequest {
  displayName?: string;
  description?: string;
  resources?: string[];
}

export interface OAuthScopeList {
  items: OAuthScope[];
  totalCount: number;
}

// ============================================================================
// Standard OIDC Scopes (for reference)
// ============================================================================

export const STANDARD_SCOPES = [
  'openid',
  'email',
  'profile',
  'phone',
  'address',
  'offline_access',
  'roles'
] as const;

export type StandardScope = typeof STANDARD_SCOPES[number];

export function isStandardScope(scope: string): scope is StandardScope {
  return STANDARD_SCOPES.includes(scope as StandardScope);
}

// ============================================================================
// OAuth API DTOs
// ============================================================================

export interface OAuthApi {
  id: string;
  name: string;
  displayName?: string;
  description?: string;
  enabled: boolean;
  scopes: string[];
  userClaims: string[];
}

export interface CreateOAuthApiRequest {
  name: string;
  displayName?: string;
  description?: string;
  enabled?: boolean;
  scopes?: string[];
  userClaims?: string[];
}

export interface UpdateOAuthApiRequest {
  displayName?: string;
  description?: string;
  enabled?: boolean;
  scopes?: string[];
  userClaims?: string[];
}

export interface OAuthApiList {
  items: OAuthApi[];
  totalCount: number;
}

export interface OAuthApiCreated extends OAuthApi {
  apiSecret: string;
}

export interface ApiSecret {
  apiSecret: string;
}
