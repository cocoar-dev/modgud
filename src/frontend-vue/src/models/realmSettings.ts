// Realm-wide settings, owned by the realm-admin (not Control-Plane).
// Mirrors src/dotnet/Cocoar.Auth.Application/DTOs/RealmSettings/RealmSettingsDtos.cs.
// Surfaced via GET/PATCH /api/admin/realm-settings — the current realm
// is implicit from the host (RealmMiddleware), no slug in the URL.

export interface RealmSettingsDto {
  SelfRegistration: SelfRegistrationDto
}

export interface UpdateRealmSettingsDto {
  SelfRegistration?: UpdateSelfRegistrationDto | null
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
