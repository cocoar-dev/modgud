// GDPR self-service models — mirror DTOs in
// src/dotnet/Modgud.Authentication/Gdpr/GdprDtos.cs.

/** Who initiated a pending deletion — mirrors the backend enum. */
export type DeletionInitiator = 'SelfService' | 'Admin'

export interface RequestDeletionDto {
  Password: string
  Reason?: string | null
}

export interface DeletionStatusDto {
  IsPending: boolean
  IsDeleted: boolean
  IsDataMasked: boolean
  /** Who initiated the pending deletion; null when not pending. */
  Initiator?: DeletionInitiator | null
  RequestedAt?: string | null
  /** Grace / retention deadline (name kept for wire compatibility). */
  ConfirmationDeadline?: string | null
}

export interface DeletionRequestResponseDto {
  RequestedAt: string
  ConfirmationDeadline: string
  Message: string
}
