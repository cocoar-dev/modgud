// Role admin models — mirror the DTOs in
// src/dotnet/Modgud.Api/Features/Roles/RolesEndpoints.cs.

export interface RoleDto {
  Id: string
  Name: string
  Description?: string | null
  /**
   * FK (ShortGuid) into App.Permissions[]. Null = pure realm-admin role.
   * When set, PermissionIds reference entries in this App's catalog.
   */
  AppId?: string | null
  /**
   * When true, the role grants `realm:admin` — the realm-wide bypass.
   * Lives outside any catalog; reserved for the System Admin role.
   */
  IsRealmAdmin: boolean
  /**
   * FKs (ShortGuids) into the linked App's catalog. Empty for pure
   * realm-admin roles or for "shell" roles that haven't been wired up
   * yet.
   */
  PermissionIds: string[]
}

export interface RolePayload {
  Name: string
  Description?: string | null
  /** App.Id (ShortGuid). Null/empty = no App link (pure realm-admin role). */
  AppId?: string | null
  IsRealmAdmin: boolean
  PermissionIds: string[]
}
