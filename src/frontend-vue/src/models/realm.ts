// Realm admin models — mirror DTOs in
// src/dotnet-next/Cocoar.Auth.Application/DTOs/Realms/RealmDtos.cs.

export interface RealmDto {
  Id: string
  Slug: string
  DisplayName: string
  Description?: string | null
  Domains: string[]
  IsControlPlane: boolean
  IsActive: boolean
  NeedsSetup: boolean
  CreatedAt: string
}

export interface CreateRealmDto {
  Slug: string
  DisplayName: string
  Description?: string | null
  Domains?: string[] | null
  IsControlPlane?: boolean
}

export interface UpdateRealmDto {
  DisplayName?: string | null
  Description?: string | null
  Domains?: string[] | null
  IsControlPlane?: boolean | null
  IsActive?: boolean | null
}

export interface RealmListDto {
  Items: RealmDto[]
  TotalCount: number
}
