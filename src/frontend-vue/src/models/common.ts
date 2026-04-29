export interface BaseEntity {
  Id: string
}

export interface RefPropertyDto {
  Id: string
  Label?: string
  /**
   * For principal references (Responsibles, CreatedBy, UpdatedBy):
   * 'Person' or 'Group'. Null/undefined for non-principal refs (e.g. Customer).
   */
  PrincipalType?: 'Person' | 'Group' | null
}

export type PrincipalType = 'Person' | 'Group'

export type EntityStatus = 'Pending' | 'Active'

export interface SelectOption<T = unknown> {
  label: string
  value: T
}

export interface DataEvent<T = unknown> {
  Subject: string
  Action: 'Created' | 'Updated' | 'Deleted' | 'Custom' | 'FullSync'
  CustomAction?: string
  Payload: T[]
  MetaData?: Record<string, unknown>
}
