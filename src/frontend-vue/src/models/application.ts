import type { UpdateAuthRateLimitsDto } from './realmSettings'
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
  // An App is one resource: the per-App ADR-0011 settings override is carried inline on
  // GET {id} / create / update (null on the list endpoint, which doesn't render it).
  Settings?: ApplicationSettingsDto | null
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
  // Optional per-App settings override, written atomically with the App.
  Settings?: ApplicationSettingsDto | null
}

export interface UpdateApplicationDto {
  DisplayName: string
  Description?: string | null
  Permissions: ApplicationPermissionInputDto[]
  Settings?: ApplicationSettingsDto | null
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

export interface ApplicationPageThemeDto {
  AccentColor?: string | null
  ErrorColor?: string | null
  ButtonRadiusPx?: number | null
  InputRadiusPx?: number | null
  CardRadiusPx?: number | null
  BodyFontFamily?: string | null
  TitleFontFamily?: string | null
}

export interface ApplicationEmailBrandingDto {
  ProductName?: string | null
  SubjectPrefix?: string | null
  Preheader?: string | null
  FooterText?: string | null
  FromName?: string | null
  /** Sender address override. Null = inherit realm, then deployment. */
  FromAddress?: string | null
  ReplyTo?: string | null
}

export interface ApplicationLoginExperienceDto {
  InternalLoginEnabled?: boolean | null
  MagicLinkEnabled?: boolean | null
  /** Ordered external-provider ShortGuid allow-list. Empty disables all. */
  LoginProviderIds?: string[] | null
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

export interface ApplicationClientSessionsDto {
  IdleLifetimeDays?: number | null
  AbsoluteLifetimeDays?: number | null
}

export interface ApplicationDcrOverrideDto extends ApplicationGrantOverrideDto {
  GcTtlDays?: number | null
  PerIpRateLimitPerHour?: number | null
  PerRealmRateLimitPerDay?: number | null
  ReservedNames?: string[] | null
}

/** Per-field requirement override: 'Off' | 'Optional' | 'Required'. A null
 * field inherits the realm requirement for that field. */
export interface ApplicationRegistrationFieldsOverrideDto {
  Username?: string | null
  Firstname?: string | null
  Lastname?: string | null
}

export interface ApplicationChangeFeedDto {
  Enabled: boolean
  MinimumRetentionAgeDays: number
  MinimumEventCount: number
}

export interface ApplicationSettingsDto {
  Origin?: ApplicationOriginDto | null
  Branding?: ApplicationBrandingSettingsDto | null
  PageTheme?: ApplicationPageThemeDto | null
  EmailBranding?: ApplicationEmailBrandingDto | null
  LoginExperience?: ApplicationLoginExperienceDto | null
  SelfRegistration?: ApplicationSelfRegistrationOverrideDto | null
  NativeGrants?: ApplicationGrantOverrideDto | null
  /** ADR 0007 — sparse rate-limit overrides for this App (null = inherit the realm). */
  AuthRateLimits?: UpdateAuthRateLimitsDto | null
  ClientSessions?: ApplicationClientSessionsDto | null
  Dcr?: ApplicationDcrOverrideDto | null
  Cimd?: ApplicationGrantOverrideDto | null
  RegistrationFields?: ApplicationRegistrationFieldsOverrideDto | null
  ChangeFeed?: ApplicationChangeFeedDto | null
}
