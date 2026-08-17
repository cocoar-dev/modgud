import type { EntityStatus } from './common'

// MG-FT-01 — the PositionPrincipal: the business identity of a position
// ("gate porter for customer XY") staffed by changing humans on shared
// terminals. Carries an account name like a service account, but never owns
// credentials — its tokens are minted through the staffing flow.

export interface PositionTerminalPolicyDto {
  Enabled: boolean
  AllowedActivationProofs: string[]
  AllowedDeviceBindings: string[]
  StaffingSessionLifetimeMinutes: number
  MaximumStaffingSessionLifetimeMinutes: number
}

export interface PositionPrincipalDto {
  Id: string
  AccountName: string
  Purpose?: string | null
  IsActive: boolean
  Status: EntityStatus
  TerminalPolicy: PositionTerminalPolicyDto
  CreatedTerminals?: TerminalDto[] | null
}

export interface PositionCreateDto {
  AccountName: string
  Purpose?: string
  IsActive?: boolean
  TerminalPolicy?: PositionTerminalPolicyUpdateDto
  /** Users authorized in the same save (staged in create mode; all-or-nothing). */
  GrantUserIds?: string[]
  /** Terminal slots set up in the same save; requires TerminalPolicy.Enabled. */
  Terminals?: TerminalCreateDto[]
}

export interface TerminalCreateDto {
  DisplayName: string
  Location?: string
  WebAuthnRpId: string
  Binding?: string
  AllowedPositionIds?: string[]
  /** Business scopes whose resources become staffing-token audiences. */
  Scopes?: string[]
  /** Apps owning the selected business scopes/APIs. */
  AppIds?: string[]
}

export interface PositionUpdateDto {
  AccountName?: string
  Purpose?: string | null
  IsActive?: boolean
  TerminalPolicy?: PositionTerminalPolicyUpdateDto
  ConfirmTerminalPolicyConsequences?: boolean
}

/** Partial policy update — omitted fields keep the persisted value. */
export interface PositionTerminalPolicyUpdateDto {
  Enabled?: boolean
  AllowedActivationProofs?: string[]
  AllowedDeviceBindings?: string[]
  StaffingSessionLifetimeMinutes?: number
  MaximumStaffingSessionLifetimeMinutes?: number
}

export interface PositionTerminalPolicyConsequencesDto {
  TerminalIds: string[]
  StaffingSessionIds: string[]
  HasConsequences: boolean
}

// ── Terminal slots (MG-FT-03) ────────────────────────────────────────────

export type TerminalStatus = 'Pending' | 'Active' | 'Disabled' | 'Revoked'

export interface TerminalDto {
  Id: string
  PositionId: string
  AllowedPositionIds: string[]
  DisplayName: string
  Location?: string | null
  ClientId: string
  WebAuthnRpId: string
  Binding: string
  Scopes: string[]
  AppIds: string[]
  Status: TerminalStatus
  Enrolled: boolean
  CreatedAt: string
  EnrolledAt?: string | null
  DisabledAt?: string | null
  RevokedAt?: string | null
  /** Only returned once, on creation of a client-secret terminal. */
  ClientSecret?: string | null
}

export interface TerminalOAuthAccessUpdateDto {
  DisplayName?: string | null
  Scopes: string[]
  AppIds: string[]
}

// ── Activation grants (MG-FT-02) ─────────────────────────────────────────

export type PositionGrantStatus = 'Active' | 'Suspended' | 'Revoked'

export interface PositionGrantDto {
  Id: string
  PositionId: string
  UserId: string
  UserDisplayName?: string | null
  UserAccountName?: string | null
  Status: PositionGrantStatus
  CreatedAt: string
  RevokedAt?: string | null
  UserHasPasskey: boolean
}

// ── Staffing sessions (MG-FT-05/07) ──────────────────────────────────────

export type StaffingSessionStatus = 'Active' | 'Ended'

/** Admin read model of one staffing shift. ActivatedByUserId is audit
 * metadata for this surface only — it never travels in tokens. */
export interface StaffingSessionDto {
  Id: string
  TerminalId: string
  ActivatedByUserId?: string | null
  ActivationProof: string
  ActivationTokenId?: string | null
  Status: StaffingSessionStatus
  StartedAt: string
  AbsoluteExpiresAt: string
  EndedAt?: string | null
  EndReason?: string | null
}

export type ActivationTokenStatus = 'PendingRegistration' | 'Active' | 'Disabled' | 'Revoked'

export interface ActivationTokenDto {
  Id: string
  Label: string
  Status: ActivationTokenStatus
  AssignedPositionIds: string[]
  RegisteredRpIds: string[]
  CreatedAt: string
}
