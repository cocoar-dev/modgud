export interface BaseEntity {
  Id: string
}

export interface RefPropertyDto {
  Id: string
  Label?: string
  /**
   * For principal references (Responsibles, CreatedBy, UpdatedBy):
   * 'person', 'group', or 'service-account'. Lowercase to match the
   * backend's Principal.Type override. Null/undefined for non-principal
   * refs (e.g. Customer).
   */
  PrincipalType?: PrincipalType | null
}

/**
 * Principal kind discriminator emitted by the backend as the lowercase
 * Principal.Type override. Pre-Phase-2C only Person + Group existed; the
 * service-account kind ships in Phase 2C for machine-to-machine identities.
 */
export type PrincipalType = 'person' | 'group' | 'service-account'

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
