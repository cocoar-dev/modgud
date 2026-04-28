import type { EntityStatus } from './common'

export interface CustomerDto {
  Id: string
  Name: string
  Important: boolean
  IsArchived: boolean
  Status: EntityStatus
}

export interface CustomerCreateDto {
  Name: string
  Important: boolean
}

export interface CustomerUpdateDto {
  Name: string
  Important: boolean
}
