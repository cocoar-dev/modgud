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
}

export interface UserCreateDto {
  Firstname: string
  Lastname: string
  Acronym?: string
  Email?: string
  UserName: string
  /** Admin opt-in to mark Identity EmailConfirmed at creation. */
  EmailConfirmed?: boolean
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
