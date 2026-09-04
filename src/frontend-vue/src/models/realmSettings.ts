// Realm-wide settings, owned by the realm-admin (not Control-Plane).
// Mirrors src/dotnet/Modgud.Application/DTOs/RealmSettings/RealmSettingsDtos.cs.
// Surfaced via GET/PATCH /api/admin/realm-settings — the current realm
// is implicit from the host (RealmMiddleware), no slug in the URL.

export interface RealmSettingsDto {
  SelfRegistration: SelfRegistrationDto
  Dcr: DcrSettingsDto
  Cimd: CimdSettingsDto
  NativeGrants: NativeGrantSettingsDto
  BrowserSessions: BrowserSessionPolicyDto
  ClientSessions: ClientSessionPolicyDto
  PositionSecurity: PositionSecuritySettingsDto
  AuthRateLimits: AuthRateLimitsDto
  Branding: BrandingSettingsDto
  EmailBranding: EmailBrandingSettingsDto
  RegistrationFields: RegistrationFieldsSettingsDto
  Deletion: DeletionSettingsDto
  Audit: AuditSettingsDto
  /** Page-builder schemas keyed by slug. Read-only here — writes use
   * the dedicated /api/admin/customization/pages/{slug} endpoints. */
  Pages: Record<string, string>
}

export interface UpdateRealmSettingsDto {
  SelfRegistration?: UpdateSelfRegistrationDto | null
  Dcr?: UpdateDcrSettingsDto | null
  Cimd?: UpdateCimdSettingsDto | null
  NativeGrants?: UpdateNativeGrantSettingsDto | null
  BrowserSessions?: UpdateBrowserSessionPolicyDto | null
  ClientSessions?: UpdateClientSessionPolicyDto | null
  PositionSecurity?: UpdatePositionSecuritySettingsDto | null
  ConfirmPositionSecurityConsequences?: boolean
  AuthRateLimits?: UpdateAuthRateLimitsDto | null
  Branding?: UpdateBrandingSettingsDto | null
  EmailBranding?: UpdateEmailBrandingSettingsDto | null
  RegistrationFields?: UpdateRegistrationFieldsSettingsDto | null
  Deletion?: UpdateDeletionSettingsDto | null
  Audit?: UpdateAuditSettingsDto | null
}

export type ProofCapability = 'IdentifiedActor' | 'PhishingResistant' | 'IndividuallyRevocable'
export type BindingCapability = 'DeviceIdentity' | 'SenderConstrained'

export interface PositionSecuritySettingsDto {
  RequiredProofCapabilities?: ProofCapability[] | null
  RequiredBindingCapabilities?: BindingCapability[] | null
}

export interface UpdatePositionSecuritySettingsDto {
  RequiredProofCapabilities?: ProofCapability[] | null
  RequiredBindingCapabilities?: BindingCapability[] | null
}

export interface PositionSecurityConsequencesDto {
  Positions: Array<{
    Id: string
    AccountName: string
    ViolatingActivationProofs: string[]
    ViolatingDeviceBindings: string[]
  }>
  TerminalIds: string[]
  StaffingSessionIds: string[]
  HasConsequences: boolean
}

export interface BrowserSessionPolicyDto {
  IdleLifetimeMinutes: number
  AbsoluteLifetimeMinutes: number
  AllowRememberMe: boolean
}

export interface UpdateBrowserSessionPolicyDto {
  IdleLifetimeMinutes?: number
  AbsoluteLifetimeMinutes?: number
  AllowRememberMe?: boolean
}

export interface ClientSessionPolicyDto {
  IdleLifetimeDays: number
  AbsoluteLifetimeDays: number
}

export interface UpdateClientSessionPolicyDto {
  IdleLifetimeDays?: number
  AbsoluteLifetimeDays?: number
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

export interface AuditSettingsDto {
  VisibilityWindowDays: number
  SecurityRetentionDays: number
}

export interface UpdateAuditSettingsDto {
  VisibilityWindowDays?: number
  SecurityRetentionDays?: number
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

export interface EmailBrandingSettingsDto {
  ProductName?: string | null
  SubjectPrefix?: string | null
  Preheader?: string | null
  FooterText?: string | null
  FromName?: string | null
  /** Sender address for outbound mail. Null = the deployment's configured sender. */
  FromAddress?: string | null
  ReplyTo?: string | null
}

export interface UpdateEmailBrandingSettingsDto extends EmailBrandingSettingsDto {}

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

// ADR 0007 — multi-dimensional auth rate limits. Every policy (one per public auth
// flow) carries ceilings per dimension: Source (effective address, a NAT-sized brake),
// SourceRegistration (silent address-spraying ceiling), Target (the mailbox — the
// defence), Client (one integration), App (the mail-cost brake). The read shape
// carries EFFECTIVE values plus the shipped Defaults and the sparse Overrides that
// are actually stored. Mirrors AuthRateLimitsDtos.cs.
export interface RateLimitRuleDto {
  PermitLimit: number
  WindowMinutes: number
  /** Token-bucket capacity; null/absent = fixed window. */
  Burst?: number | null
  Enabled?: boolean
  /** Read-only marker: evaluated and counted, never rejects (ADR 0008 login spray signal). */
  SignalOnly?: boolean
}

export type RateLimitDimensionKey = 'Source' | 'SourceRegistration' | 'Target' | 'Client' | 'App' | 'Device'
export type RateLimitEnforcementMode = 'Enforce' | 'LogOnly'

export type PolicyLimitsDto = Partial<Record<RateLimitDimensionKey, RateLimitRuleDto | null>>

export interface AuthRateLimitsDto {
  Policies: Record<string, PolicyLimitsDto>
  Defaults: Record<string, PolicyLimitsDto>
  SourceAllowlist: string[]
  Mode: RateLimitEnforcementMode
  LegacyOverridesPresent: boolean
  Overrides?: UpdateAuthRateLimitsDto | null
}

// Merge-patch v2 per dimension: absent = unchanged, null = back to the baseline,
// value = override. A null policy drops every override of that policy.
export type UpdatePolicyLimitsDto = Partial<Record<RateLimitDimensionKey, RateLimitRuleDto | null>>

export interface UpdateAuthRateLimitsDto {
  Policies?: Record<string, UpdatePolicyLimitsDto | null>
  /** null = clear the list. */
  SourceAllowlist?: string[] | null
  /** null = automatic (enforce; log-only while legacy per-IP rules exist). */
  Mode?: RateLimitEnforcementMode | null
  ClearLegacy?: boolean
}

/** Editor model: policy → dimension → rule, null = inherit the baseline. */
export type RateLimitOverrides = Record<string, Record<RateLimitDimensionKey, RateLimitRuleDto | null>>

export const RATE_LIMIT_DIMENSIONS: { key: RateLimitDimensionKey; fallback: string; hint: string }[] = [
  { key: 'Source', fallback: 'Source', hint: 'effective address — a coarse brake sized for shared addresses' },
  { key: 'SourceRegistration', fallback: 'Sign-ups per source', hint: 'silent — unknown addresses from one source' },
  { key: 'Target', fallback: 'Target', hint: 'per mailbox / username — the defence' },
  { key: 'Client', fallback: 'Client', hint: 'per OAuth client — bounds one integration' },
  { key: 'App', fallback: 'App', hint: 'per Application — the cost brake' },
  { key: 'Device', fallback: 'Device', hint: 'login only — failures per browser the user signed in from before' },
]

export const RATE_LIMIT_POLICIES: { key: string; labelKey: string; fallback: string }[] = [
  { key: 'native-otp', labelKey: 'admin.rateLimits.policy.nativeOtp', fallback: 'Native OTP request / register' },
  { key: 'self-registration', labelKey: 'admin.rateLimits.policy.selfRegistration', fallback: 'Web self-registration' },
  { key: 'magic-link', labelKey: 'admin.rateLimits.policy.magicLink', fallback: 'Magic-link request' },
  { key: 'password-reset', labelKey: 'admin.rateLimits.policy.passwordReset', fallback: 'Password-reset request' },
  { key: 'email-verification', labelKey: 'admin.rateLimits.policy.emailVerification', fallback: 'Email verification resend' },
  { key: 'email-otp', labelKey: 'admin.rateLimits.policy.emailOtp', fallback: 'Email-OTP code verify' },
  { key: 'passkey-begin', labelKey: 'admin.rateLimits.policy.passkeyBegin', fallback: 'Passkey ceremony begin / enroll' },
  { key: 'oauth-token', labelKey: 'admin.rateLimits.policy.oauthToken', fallback: 'OAuth token endpoint' },
  { key: 'bootstrap', labelKey: 'admin.rateLimits.policy.bootstrap', fallback: 'First-admin bootstrap' },
  { key: 'login', labelKey: 'admin.rateLimits.policy.login', fallback: 'Password login (failures)' },
]

export function emptyRateLimitOverrides(): RateLimitOverrides {
  const out: RateLimitOverrides = {}
  for (const p of RATE_LIMIT_POLICIES)
    out[p.key] = { Source: null, SourceRegistration: null, Target: null, Client: null, App: null, Device: null }
  return out
}

/** The stored sparse overrides → editor model (missing = inherit). */
export function overridesFromUpdate(u?: UpdateAuthRateLimitsDto | null): RateLimitOverrides {
  const out = emptyRateLimitOverrides()
  for (const [policy, limits] of Object.entries(u?.Policies ?? {})) {
    if (!limits) continue
    out[policy] ??= { Source: null, SourceRegistration: null, Target: null, Client: null, App: null, Device: null }
    for (const d of RATE_LIMIT_DIMENSIONS) {
      const rule = limits[d.key]
      if (rule) out[policy][d.key] = { ...rule }
    }
  }
  return out
}

/** Editor model → the sparse full-replace shape (Application override). */
export function sparseRateLimitPolicies(o: RateLimitOverrides): Record<string, UpdatePolicyLimitsDto> | undefined {
  const out: Record<string, UpdatePolicyLimitsDto> = {}
  for (const [policy, dims] of Object.entries(o)) {
    const entry: UpdatePolicyLimitsDto = {}
    for (const d of RATE_LIMIT_DIMENSIONS) if (dims[d.key]) entry[d.key] = { ...dims[d.key]! }
    if (Object.keys(entry).length) out[policy] = entry
  }
  return Object.keys(out).length ? out : undefined
}

/** Editor model diff → merge-patch (null = override removed). */
export function diffRateLimitOverrides(before: RateLimitOverrides, after: RateLimitOverrides): Record<string, UpdatePolicyLimitsDto> {
  const out: Record<string, UpdatePolicyLimitsDto> = {}
  const same = (a: RateLimitRuleDto | null, b: RateLimitRuleDto | null) =>
    JSON.stringify(a ?? null) === JSON.stringify(b ?? null)
  for (const policy of new Set([...Object.keys(before), ...Object.keys(after)])) {
    const entry: UpdatePolicyLimitsDto = {}
    for (const d of RATE_LIMIT_DIMENSIONS) {
      const b = before[policy]?.[d.key] ?? null
      const a = after[policy]?.[d.key] ?? null
      if (!same(a, b)) entry[d.key] = a ? { ...a } : null
    }
    if (Object.keys(entry).length) out[policy] = entry
  }
  return out
}
