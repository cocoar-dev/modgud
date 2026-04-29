// Application admin models — mirror the DTOs in
// src/dotnet/Cocoar.Auth.Api/Features/Admin/Apps/AppsEndpoints.cs.
//
// "Application" is the user-facing concept. The backend C# class is named
// `App` to avoid colliding with the `Cocoar.Auth.Application` CQRS-layer
// namespace; on the frontend we keep the user-facing word.

export interface ApplicationDto {
  Id: string
  Slug: string
  DisplayName: string
  Description?: string | null
  Resources: string[]
  IsSystem: boolean
}

export interface ApplicationLookupDto {
  Id: string
  Slug: string
  DisplayName: string
}

export interface CreateApplicationDto {
  Slug: string
  DisplayName: string
  Description?: string | null
  Resources: string[]
}

export interface UpdateApplicationDto {
  DisplayName: string
  Description?: string | null
  Resources: string[]
}
