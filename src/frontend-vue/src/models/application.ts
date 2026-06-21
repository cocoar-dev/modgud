// Application admin models — mirror the DTOs in
// src/dotnet/Modgud.Api/Features/Admin/Apps/AppsEndpoints.cs.
//
// "Application" is the user-facing concept. The backend C# class is named
// `App` to avoid colliding with the `Modgud.Application` CQRS-layer
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

// ── ADR-0011: per-Application settings overrides ────────────────────────────
// Mirrors Modgud.Application.DTOs.Applications.ApplicationSettingsDto. Sparse:
// a null section = "inherits the realm"; on PATCH a null section = "no change",
// and a provided section replaces that App's override (null field = inherit).

export interface ApplicationOriginDto {
  Subdomain?: string | null
}

export interface ApplicationBrandingSettingsDto {
  ProductName?: string | null
  PrimaryColor?: string | null
  LogoAssetId?: string | null
  LogoUrl?: string | null // read-only
  FaviconAssetId?: string | null
  FaviconUrl?: string | null // read-only
}

export interface ApplicationEmailBrandingDto {
  ProductName?: string | null
}

export interface ApplicationSelfRegistrationOverrideDto {
  /** 'Off' | 'JitOnOtp' | 'ExplicitEndpoint' */
  Posture?: string | null
  Enabled?: boolean | null
  RequireEmailVerification?: boolean | null
  AllowedEmailDomains?: string[] | null
  RequireAdminApproval?: boolean | null
  DefaultGroupIds?: string[] | null
  TermsOfServiceUrl?: string | null
  PrivacyPolicyUrl?: string | null
}

export interface ApplicationGrantOverrideDto {
  Enabled?: boolean | null
  AccessTokenLifetimeMinutes?: number | null
  RefreshTokenLifetimeDays?: number | null
}

export interface ApplicationDcrOverrideDto extends ApplicationGrantOverrideDto {
  GcTtlDays?: number | null
  PerIpRateLimitPerHour?: number | null
  PerRealmRateLimitPerDay?: number | null
  ReservedNames?: string[] | null
}

export interface ApplicationSettingsDto {
  Origin?: ApplicationOriginDto | null
  Branding?: ApplicationBrandingSettingsDto | null
  EmailBranding?: ApplicationEmailBrandingDto | null
  SelfRegistration?: ApplicationSelfRegistrationOverrideDto | null
  NativeGrants?: ApplicationGrantOverrideDto | null
  Dcr?: ApplicationDcrOverrideDto | null
  Cimd?: ApplicationGrantOverrideDto | null
}
