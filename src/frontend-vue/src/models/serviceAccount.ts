import type { EntityStatus } from './common'
import type { AccessTokenType } from './oauth'

export interface ServiceAccountDto {
  Id: string
  AccountName: string
  Purpose?: string | null
  IsActive: boolean
  Status: EntityStatus
  /** Present exactly once when create also issued the initial credential. */
  InitialCredential?: ServiceAccountCredentialIssuedDto
}

export interface ServiceAccountCreateDto {
  AccountName: string
  Purpose?: string
  IsActive?: boolean
  InitialCredential?: IssueServiceAccountCredentialDto
}

export interface ServiceAccountUpdateDto {
  AccountName?: string
  Purpose?: string | null
  IsActive?: boolean
}

// ── Service-Account credentials ──────────────────────────────────────────
//
// A "credential" on a SA is a confidential OAuth client pinned to the SA
// with the single `client_credentials` grant. The wire shape is the full
// OAuthClientDto (so the M2M-discovery view in the Clients grid can reuse
// it), but the admin UI only exposes a narrow surface: DisplayName,
// Scopes, AppIds, AccessTokenLifetime, Enabled.

export interface IssueServiceAccountCredentialDto {
  /** Optional override. Defaults to `{accountName}.{8-char ShortGuid}`. */
  ClientId?: string
  DisplayName?: string
  Scopes: string[]
  AppIds: string[]
  AccessTokenLifetime?: number
  Enabled?: boolean
  /** Reference (opaque, instantly revocable — default) vs Jwt (self-validating). */
  AccessTokenType?: AccessTokenType
}

export interface UpdateServiceAccountCredentialDto {
  DisplayName?: string
  Scopes?: string[]
  AppIds?: string[]
  AccessTokenLifetime?: number
  Enabled?: boolean
  AccessTokenType?: AccessTokenType
}

import type { OAuthClientDto } from './oauth'

export interface ServiceAccountCredentialIssuedDto {
  Credential: OAuthClientDto
  ClientSecret: string
}
