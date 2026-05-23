import type { EntityStatus } from './common'

export interface ServiceAccountDto {
  Id: string
  AccountName: string
  Purpose?: string | null
  IsActive: boolean
  Status: EntityStatus
}

export interface ServiceAccountCreateDto {
  AccountName: string
  Purpose?: string
}

export interface ServiceAccountUpdateDto {
  AccountName?: string
  Purpose?: string | null
  IsActive?: boolean
}
