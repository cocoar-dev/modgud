// Login-Provider admin models — mirror DTOs in
// src/dotnet-next/Cocoar.Auth.Application/DTOs/LoginProviders/LoginProviderDtos.cs.
// LoginProviderType is serialized as a string (JsonStringEnumConverter).

export type LoginProviderType = 'Internal' | 'OpenIdConnect'

export interface LoginProviderDto {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Type: LoginProviderType
  Configuration: Record<string, string>
  IsBuiltIn: boolean
}

export interface CreateLoginProviderDto {
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Type?: LoginProviderType
  Configuration?: Record<string, string>
}

export interface UpdateLoginProviderDto {
  Name?: string | null
  DisplayName?: string | null
  Description?: string | null
  Configuration?: Record<string, string> | null
}

export interface LoginProviderListDto {
  Items: LoginProviderDto[]
  TotalCount: number
}
