// Realm admin models — mirror DTOs in
// src/dotnet/Modgud.Application/DTOs/Realms/RealmDtos.cs.
//
// Tenant-owned settings (self-registration etc.) live in `realmSettings.ts`
// and are addressed via `/api/admin/realm-settings`, not here.

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

export interface InitialAdminDto {
  UserName: string
  Email: string
  Firstname?: string | null
  Lastname?: string | null
}

export interface InitialAdminInviteDto {
  UserName: string
  Email: string
  ExpiresAt: string
  MagicLinkUrl: string
}

export interface CreateRealmDto {
  Slug: string
  DisplayName: string
  Description?: string | null
  Domains?: string[] | null
  IsControlPlane?: boolean
  InitialAdmin: InitialAdminDto
}

export interface CreatedRealmDto {
  Realm: RealmDto
  InitialAdminInvite: InitialAdminInviteDto
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
