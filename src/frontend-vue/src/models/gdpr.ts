// GDPR self-service models — mirror DTOs in
// src/dotnet-next/Modgud.Authentication/Gdpr/GdprDtos.cs.

export interface RequestDeletionDto {
  Password: string
  Reason?: string | null
}

export interface DeletionStatusDto {
  IsPending: boolean
  IsDeleted: boolean
  IsDataMasked: boolean
  RequestedAt?: string | null
  ConfirmationDeadline?: string | null
}

export interface DeletionRequestResponseDto {
  RequestedAt: string
  ConfirmationDeadline: string
  Message: string
}
