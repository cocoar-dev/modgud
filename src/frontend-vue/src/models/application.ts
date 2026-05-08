// Application admin models — mirror the DTOs in
// src/dotnet/Cocoar.Auth.Api/Features/Admin/Apps/AppsEndpoints.cs.
//
// "Application" is the user-facing concept. The backend C# class is named
// `App` to avoid colliding with the `Cocoar.Auth.Application` CQRS-layer
// namespace; on the frontend we keep the user-facing word.

export interface ApplicationPermissionDto {
  /** Stable id (ShortGuid). Generated server-side on first save. */
  Id: string
  Resource: string
  Action: string
  Description?: string | null
}

export interface ApplicationDto {
  Id: string
  Slug: string
  DisplayName: string
  Description?: string | null
  Permissions: ApplicationPermissionDto[]
  IsSystem: boolean
}

export interface ApplicationLookupDto {
  Id: string
  Slug: string
  DisplayName: string
}

/**
 * Permission entry on the create / update payload. `Id` is optional on
 * create; on update keep the server-issued id for stable identity, omit
 * for new entries.
 */
export interface ApplicationPermissionInputDto {
  Id?: string | null
  Resource: string
  Action: string
  Description?: string | null
}

export interface CreateApplicationDto {
  Slug: string
  DisplayName: string
  Description?: string | null
  Permissions: ApplicationPermissionInputDto[]
}

export interface UpdateApplicationDto {
  DisplayName: string
  Description?: string | null
  Permissions: ApplicationPermissionInputDto[]
}
