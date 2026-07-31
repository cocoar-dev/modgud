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
  /** The realm's canonical public host — always one of `Domains`. Drives all
   * outbound links and is the WebAuthn RP ID for this realm's passkeys. */
  PrimaryDomain: string
  IsControlPlane: boolean
  IsActive: boolean
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
  /** Routing domains — REQUIRED (at least one); a realm with no domain cannot
   * route requests or build outbound links. */
  Domains?: string[] | null
  /** Optional. Canonical public host for outbound links + WebAuthn RP. When set
   * it must be one of `Domains`; when omitted the first `Domains` entry is used. */
  PrimaryDomain?: string | null
  /** Initial activation state. Defaults to true. */
  IsActive?: boolean | null
}

export interface CreatedRealmDto {
  Realm: RealmDto
  InitialAdminInvite?: InitialAdminInviteDto | null
}

export interface UpdateRealmDto {
  DisplayName?: string | null
  Description?: string | null
  Domains?: string[] | null
  /** Optional. When set it must be one of the resulting domain set. Changing it
   * invalidates the realm's existing passkeys (they're bound to the old host). */
  PrimaryDomain?: string | null
  IsActive?: boolean | null
}

export interface RealmListDto {
  Items: RealmDto[]
  TotalCount: number
}
