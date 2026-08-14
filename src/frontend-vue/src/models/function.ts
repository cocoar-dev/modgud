import type { EntityStatus } from './common'

// MG-FT-01 — the FunctionPrincipal: the business identity of a function
// ("gate porter for customer XY") staffed by changing humans on shared
// terminals. Carries an account name like a service account, but never owns
// credentials — its tokens are minted through the staffing flow.

export interface FunctionTerminalPolicyDto {
  Enabled: boolean
  StaffingSessionLifetimeMinutes: number
  MaximumStaffingSessionLifetimeMinutes: number
}

export interface FunctionPrincipalDto {
  Id: string
  AccountName: string
  Purpose?: string | null
  IsActive: boolean
  Status: EntityStatus
  TerminalPolicy: FunctionTerminalPolicyDto
}

export interface FunctionCreateDto {
  AccountName: string
  Purpose?: string
  IsActive?: boolean
  TerminalPolicy?: FunctionTerminalPolicyUpdateDto
  /** Users authorized in the same save (staged in create mode; all-or-nothing). */
  GrantUserIds?: string[]
}

export interface FunctionUpdateDto {
  AccountName?: string
  Purpose?: string | null
  IsActive?: boolean
  TerminalPolicy?: FunctionTerminalPolicyUpdateDto
}

/** Partial policy update — omitted fields keep the persisted value. */
export interface FunctionTerminalPolicyUpdateDto {
  Enabled?: boolean
  StaffingSessionLifetimeMinutes?: number
  MaximumStaffingSessionLifetimeMinutes?: number
}

// ── Terminal slots (MG-FT-03) ────────────────────────────────────────────

export type TerminalStatus = 'Pending' | 'Active' | 'Disabled' | 'Revoked'

export interface TerminalDto {
  Id: string
  FunctionId: string
  DisplayName: string
  Location?: string | null
  ClientId: string
  WebAuthnRpId: string
  Status: TerminalStatus
  Enrolled: boolean
  CreatedAt: string
  EnrolledAt?: string | null
  DisabledAt?: string | null
  RevokedAt?: string | null
}

// ── Activation grants (MG-FT-02) ─────────────────────────────────────────

export type FunctionGrantStatus = 'Active' | 'Suspended' | 'Revoked'

export interface FunctionGrantDto {
  Id: string
  FunctionId: string
  UserId: string
  UserDisplayName?: string | null
  UserAccountName?: string | null
  Status: FunctionGrantStatus
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
  ActivatedByUserId: string
  Status: StaffingSessionStatus
  StartedAt: string
  AbsoluteExpiresAt: string
  EndedAt?: string | null
  EndReason?: string | null
}
