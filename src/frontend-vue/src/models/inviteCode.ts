/**
 * ADR-0012 — registration invite codes (the InviteCode self-registration
 * posture). Codes are app-scoped: the admin surface lives under
 * `/api/app/{appId}/invite-codes`.
 */

/** Read/list projection — never carries the plaintext or the hash. */
export interface InviteCodeDto {
  Id: string
  AppId: string
  /** null = bearer (anyone with the code); otherwise bound to this email. */
  BoundEmail: string | null
  CreatedAt: string
  ExpiresAt: string
  CreatedBySubject: string
  UsedAt: string | null
  UsedByUserId: string | null
  /** 'Open' | 'Used' | 'Expired'. */
  Status: string
}

export interface MintInviteCodesDto {
  Count: number
  /** null = bearer codes. */
  BoundEmail?: string | null
  /** null = the Modgud default of 14 days. */
  ExpiresInDays?: number | null
}

/** The plaintext codes — returned exactly once, only hashes are stored. */
export interface MintInviteCodesResultDto {
  Codes: string[]
}
