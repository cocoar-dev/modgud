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
