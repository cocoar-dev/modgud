// Realm-wide settings, owned by the realm-admin (not Control-Plane).
// Mirrors src/dotnet/Cocoar.Auth.Application/DTOs/RealmSettings/RealmSettingsDtos.cs.
// Surfaced via GET/PATCH /api/admin/realm-settings — the current realm
// is implicit from the host (RealmMiddleware), no slug in the URL.

export interface RealmSettingsDto {
  SelfRegistration: SelfRegistrationDto
  Dcr: DcrSettingsDto
  Branding: BrandingSettingsDto
}

export interface UpdateRealmSettingsDto {
  SelfRegistration?: UpdateSelfRegistrationDto | null
  Dcr?: UpdateDcrSettingsDto | null
  Branding?: UpdateBrandingSettingsDto | null
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
