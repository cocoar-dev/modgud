// Realm-wide settings, owned by the realm-admin (not Control-Plane).
// Mirrors src/dotnet/Modgud.Application/DTOs/RealmSettings/RealmSettingsDtos.cs.
// Surfaced via GET/PATCH /api/admin/realm-settings — the current realm
// is implicit from the host (RealmMiddleware), no slug in the URL.

export interface RealmSettingsDto {
  SelfRegistration: SelfRegistrationDto
  Dcr: DcrSettingsDto
  Cimd: CimdSettingsDto
  NativeGrants: NativeGrantSettingsDto
  Branding: BrandingSettingsDto
  RegistrationFields: RegistrationFieldsSettingsDto
  Deletion: DeletionSettingsDto
  /** Page-builder schemas keyed by slug. Read-only here — writes use
   * the dedicated /api/admin/customization/pages/{slug} endpoints. */
  Pages: Record<string, string>
}

export interface UpdateRealmSettingsDto {
  SelfRegistration?: UpdateSelfRegistrationDto | null
  Dcr?: UpdateDcrSettingsDto | null
  Cimd?: UpdateCimdSettingsDto | null
  NativeGrants?: UpdateNativeGrantSettingsDto | null
  Branding?: UpdateBrandingSettingsDto | null
  RegistrationFields?: UpdateRegistrationFieldsSettingsDto | null
  Deletion?: UpdateDeletionSettingsDto | null
}

// Per-realm policy for which identity fields are required when an account is
// created. Email is always required and is not represented. Each value is one
// of 'Off' | 'Optional' | 'Required'. Default (unconfigured) = all Optional.
export type FieldRequirement = 'Off' | 'Optional' | 'Required'

export interface RegistrationFieldsSettingsDto {
  Username: FieldRequirement
  Firstname: FieldRequirement
  Lastname: FieldRequirement
}

export interface UpdateRegistrationFieldsSettingsDto {
  Username?: string
  Firstname?: string
  Lastname?: string
}

// Per-realm account-deletion policy. GraceDays drives the self-service
// auto-erase window, ReminderLeadDays the "about to be deleted" reminder,
// AdminRetentionDays the admin recycle-bin retention, AutoPurgeEnabled
// whether the bin is emptied automatically at retention expiry.
export interface DeletionSettingsDto {
  GraceDays: number
  ReminderLeadDays: number
  AdminRetentionDays: number
  AutoPurgeEnabled: boolean
}

export interface UpdateDeletionSettingsDto {
  GraceDays?: number
  ReminderLeadDays?: number
  AdminRetentionDays?: number
  AutoPurgeEnabled?: boolean
}

// Read shape for the Branding sub-section. LogoUrl/FaviconUrl are
// server-resolved from the asset id (handy for the SPA to drop into
// <img src>); LogoAssetId/FaviconAssetId round-trip back into the
// admin form. All nullable — null = SPA falls back to Cocoar defaults.
export interface BrandingSettingsDto {
  ProductName?: string | null
  LogoAssetId?: string | null
  LogoUrl?: string | null
  FaviconAssetId?: string | null
  FaviconUrl?: string | null
  PrimaryColor?: string | null
}

// PATCH shape. Tri-state per field: undefined/null = no change,
// "" = clear (revert to Cocoar default), other = replace. The
// asset-id fields take ShortGuid strings.
export interface UpdateBrandingSettingsDto {
  ProductName?: string | null
  LogoAssetId?: string | null
  FaviconAssetId?: string | null
  PrimaryColor?: string | null
}

// Read shape. CaptchaSecretSet is the only signal the SPA gets about the
// per-realm secret — the plaintext never crosses the wire.
export interface SelfRegistrationDto {
  Enabled: boolean
  RequireEmailVerification: boolean
  AllowedEmailDomains?: string[] | null
  RequireAdminApproval: boolean
  DefaultGroupIds?: string[] | null
  TermsOfServiceUrl?: string | null
  PrivacyPolicyUrl?: string | null
  CaptchaEnabled: boolean
  CaptchaSiteKey?: string | null
  CaptchaSecretSet: boolean
}

// PATCH shape. Every field optional — omit = no change. CaptchaSecret has
// three states: undefined/null = no change, "" = clear (revert to default),
// "xxx" = replace.
export interface UpdateSelfRegistrationDto {
  Enabled?: boolean
  RequireEmailVerification?: boolean
  AllowedEmailDomains?: string[] | null
  RequireAdminApproval?: boolean
  DefaultGroupIds?: string[] | null
  TermsOfServiceUrl?: string | null
  PrivacyPolicyUrl?: string | null
  CaptchaEnabled?: boolean
  CaptchaSiteKey?: string | null
  CaptchaSecret?: string | null
}

// Dynamic Client Registration (RFC 7591) — per-realm config that gates
// the public /connect/register endpoint. The triple-opt-in design also
// requires AllowDynamicRegistration on at least one OAuthApi AND
// AllowDynamicRegistrationClients on at least one OAuthScope for DCR
// clients to be able to mint usable tokens — flipping the master toggle
// here is necessary but not sufficient.
export interface DcrSettingsDto {
  Enabled: boolean
  AccessTokenLifetimeMinutes: number
  RefreshTokenLifetimeDays: number
  GcTtlDays: number
  PerIpRateLimitPerHour: number
  PerRealmRateLimitPerDay: number
  ReservedNames?: string[] | null
}

export interface UpdateDcrSettingsDto {
  Enabled?: boolean
  AccessTokenLifetimeMinutes?: number
  RefreshTokenLifetimeDays?: number
  GcTtlDays?: number
  PerIpRateLimitPerHour?: number
  PerRealmRateLimitPerDay?: number
  ReservedNames?: string[] | null
}

// Client ID Metadata Documents (CIMD) — the MCP-preferred
// registration path. The client_id IS an https URL the server fetches +
// validates on demand; no stored record, no client_secret, identity bound
// to domain ownership. Per-realm master toggle, off by default. Like DCR,
// a CIMD client still has to clear the per-OAuthApi AllowDynamicRegistration
// opt-in before it can mint a usable token — this toggle is necessary, not
// sufficient.
export interface CimdSettingsDto {
  Enabled: boolean
  AccessTokenLifetimeMinutes: number
  RefreshTokenLifetimeDays: number
}

export interface UpdateCimdSettingsDto {
  Enabled?: boolean
  AccessTokenLifetimeMinutes?: number
  RefreshTokenLifetimeDays?: number
}

// Native passwordless token grants (ADR-0010) — the cookieless
// urn:cocoar:otp / :magic / :passkey grants on /connect/token. Per-realm
// master toggle, off by default. Necessary but not sufficient: a client
// also needs the matching gt:urn:cocoar:* grant-type permission before it
// can use these grants — flipping this toggle just unlocks the seam.
export interface NativeGrantSettingsDto {
  Enabled: boolean
  AccessTokenLifetimeMinutes: number
  RefreshTokenLifetimeDays: number
}

export interface UpdateNativeGrantSettingsDto {
  Enabled?: boolean
  AccessTokenLifetimeMinutes?: number
  RefreshTokenLifetimeDays?: number
}
