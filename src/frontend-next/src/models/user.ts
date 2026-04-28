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
  /** IdpConfig ShortGuids of active external-identity links. Empty = local-only. */
  ExternalIdpConfigIds: string[]
  Status: EntityStatus
}

export interface UserCreateDto {
  Firstname: string
  Lastname: string
  Acronym?: string
  Email?: string
  UserName: string
}

export interface UserUpdateDto {
  Firstname: string
  Lastname: string
  Acronym?: string
  Email?: string
}

export interface UserLookupDto {
  Id: string
  Label: string
  UserName: string
}
