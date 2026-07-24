import type { EntityStatus } from './common'

export interface UserDto {
  Id: string
  Firstname: string
  Lastname: string
  Acronym?: string
  Email?: string
  UserName: string
  IsActive: boolean
  HasPassword: boolean
  EmailConfirmed: boolean
  /** LoginProvider ShortGuids of active external-identity links. Empty = local-only. */
  ExternalLoginProviderIds: string[]
  Status: EntityStatus
  /** True when the user is in the recycle bin / self-service grace window.
   *  Joined in by the list + by-id queries (not carried on live SignalR
   *  snapshots — correct on full load, may lag on a pushed update). */
  IsDeletionPending?: boolean
  /** "SelfService" | "Admin" — who initiated the pending deletion. */
  DeletionInitiator?: string | null
  /** Grace / retention deadline of the pending deletion (ISO string). */
  DeletionDeadline?: string | null
}

export interface UserCreateDto {
  Firstname: string
  Lastname: string
  Acronym?: string
  Email?: string
  UserName: string
  /** Initial password. Omitted = no password (magic link / passkey / external IdP only). */
  Password?: string
  /** Admin opt-in to mark Identity EmailConfirmed at creation. */
  EmailConfirmed?: boolean
  /** Whether the account can sign in. Omitted = active. */
  IsActive?: boolean
}

export interface UserUpdateDto {
  Firstname: string
  Lastname: string
  Acronym?: string
  Email?: string
  /** Admin override of Identity EmailConfirmed. Omitted = leave unchanged. */
  EmailConfirmed?: boolean
}

export interface UserLookupDto {
  Id: string
  Label: string
  UserName: string
}
