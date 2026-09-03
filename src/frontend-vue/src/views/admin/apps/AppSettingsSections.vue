<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import {
  CoarNotice,
  CoarTextInput, CoarNumberInput, CoarFormField, CoarCheckbox, CoarSelect, CoarButton, useDialog,
  CoarTabGroup, CoarTab, CoarMultiSelect,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import EditableStringList from '@/components/EditableStringList.vue'
import AuthRateLimitsEditor from '@/components/AuthRateLimitsEditor.vue'
import {
  emptyRateLimitOverrides, overridesFromUpdate, sparseRateLimitPolicies,
  type RateLimitEnforcementMode, type RateLimitOverrides,
} from '@/models/realmSettings'
import { useGroupStore } from '@/stores/group.store'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import { useAppPagesApi, type AppSlotDto } from '@/composables/usePagesApi'
import type { ApplicationSettingsDto } from '@/models/application'
import AssetPicker from '@/components/AssetPicker.vue'
import ColorField from '@/components/ColorField.vue'
import type { AssetDto } from '@/models/assets'
import BrandingPreview from '@/components/BrandingPreview.vue'

// ADR-0011 per-App settings override sections, extracted from the old standalone
// ApplicationSettingsModal so the single App modal (AppDetails) can carry them as a
// tab. The parent owns load + save; this component owns the form. Populate it via the
// `modelValue` prop (the App's current Settings) and read it back via the exposed
// `build()` — the exact same override/inherit shape the per-App settings doc uses.
const { t } = useI18n()
const dialog = useDialog()
const props = defineProps<{
  modelValue?: ApplicationSettingsDto | null
  applicationId?: string
  applicationName?: string
}>()

const groupStore = useGroupStore()
const loginProviderStore = useLoginProviderStore()
const appConfig = useAppConfigStore()
const activeTab = ref<'origin' | 'registration' | 'sessions' | 'grants' | 'rateLimits' | 'oauth' | 'sync' | 'pages'>('origin')

const groupOptions = ref<{ value: string; label: string }[]>([])
const loginProviderOptions = ref<{ value: string; label: string }[]>([])

// Per-section "override" toggle (on → this App overrides the realm; off → inherit).
// Numbers are kept as strings (empty = inherit that field).
const f = reactive({
  origin: { override: false, subdomain: '' },
  branding: {
    override: false, productName: '', primaryColor: '',
    logoAssetId: null as string | null, logoUrl: null as string | null,
    faviconAssetId: null as string | null, faviconUrl: null as string | null,
  },
  pageTheme: {
    override: false,
    accentColor: '', errorColor: '',
    buttonRadius: '', inputRadius: '', cardRadius: '',
    bodyFontFamily: '', titleFontFamily: '',
  },
  emailBranding: { override: false, productName: '', subjectPrefix: '', preheader: '', footerText: '', fromName: '', fromAddress: '', replyTo: '' },
  loginExperience: { override: false, internal: true, magicLink: true, providerIds: [] as string[] },
  selfReg: {
    override: false,
    posture: '' as '' | 'Off' | 'JitOnOtp' | 'ExplicitEndpoint' | 'InviteCode',
    enabled: false,
    requireEmailVerification: true,
    requireAdminApproval: false,
    allowedEmailDomains: [] as string[],
    defaultGroupIds: [] as string[],
    termsOfServiceUrl: '',
    privacyPolicyUrl: '',
  },
  registrationFields: {
    override: false,
    username: '' as '' | 'Off' | 'Optional' | 'Required',
    firstname: '' as '' | 'Off' | 'Optional' | 'Required',
    lastname: '' as '' | 'Off' | 'Optional' | 'Required',
  },
  clientSessions: { override: false, idle: '', absolute: '' },
  nativeGrants: { override: false, enabled: false, access: '', refresh: '' },
  rateLimits: {
    override: false,
    overrides: emptyRateLimitOverrides() as RateLimitOverrides,
    allowlistOverride: false,
    allowlist: [] as string[],
    mode: 'inherit' as 'inherit' | RateLimitEnforcementMode,
  },
  dcr: {
    override: false, enabled: false, access: '', refresh: '',
    reservedNames: [] as string[], perIp: '', perRealm: '',
  },
  cimd: { override: false, enabled: false, access: '', refresh: '' },
  changeFeed: {
    enabled: false,
    retentionAgeDays: 7 as number | null,
    minimumEventCount: 1000 as number | null,
  },
})

const postureOptions = [
  { value: '', label: t('admin.appSettings.inherit', {}, '(inherit from realm)') },
  { value: 'Off', label: t('admin.appSettings.posture.off', {}, 'Off — no self-registration') },
  { value: 'JitOnOtp', label: t('admin.appSettings.posture.jit', {}, 'JIT-on-OTP (passwortlos)') },
  { value: 'ExplicitEndpoint', label: t('admin.appSettings.posture.explicit', {}, 'Expliziter Endpoint') },
  { value: 'InviteCode', label: t('admin.appSettings.posture.inviteCode', {}, 'Invite code (invite-only)') },
]

const requirementOptions = [
  { value: '', label: t('admin.appSettings.inherit', {}, '(inherit from realm)') },
  { value: 'Off', label: t('admin.regFields.off', {}, 'Aus') },
  { value: 'Optional', label: t('admin.regFields.optional', {}, 'Optional') },
  { value: 'Required', label: t('admin.regFields.required', {}, 'Required') },
]

function numStr(n?: number | null): string {
  return n === null || n === undefined ? '' : String(n)
}
function parseNum(s: string): number | null {
  const v = s.trim()
  if (v === '') return null
  const n = Number(v)
  return Number.isFinite(n) ? n : null
}

// Realm defaults — the value each section inherits when it is NOT overridden.
// Shown greyed in the (always-visible) fields so a setting stays findable and its
// effective value is visible without ticking "override" first (Modal & Form Contract R1).
const realmSettingsStore = useRealmSettingsStore()
// ADR 0007 — the App editor inherits the realm's EFFECTIVE limits.
const realmRateLimitBaseline = computed(() => realmSettingsStore.settings?.AuthRateLimits?.Policies ?? {})
const appRateLimitModeOptions = computed(() => [
  { value: 'inherit', label: t('admin.rateLimits.mode.inherit', {}, 'Inherit from realm') },
  { value: 'Enforce', label: t('admin.rateLimits.mode.enforce', {}, 'Enforce') },
  { value: 'LogOnly', label: t('admin.rateLimits.mode.logOnly', {}, 'Log only (evaluate and count, never reject)') },
])
const inh = computed(() => {
  const r = realmSettingsStore.settings
  return {
    // No realm equivalent — inheriting means "realm primary domain / realm default".
    origin: { subdomain: '' },
    branding: {
      productName: r?.Branding?.ProductName ?? '',
      primaryColor: r?.Branding?.PrimaryColor ?? '',
    },
    // Application-only by design: there is no realm theme to inherit. Empty
    // values leave the matching Cocoar UI token at its library default.
    pageTheme: {
      accentColor: '', errorColor: '',
      buttonRadius: '', inputRadius: '', cardRadius: '',
      bodyFontFamily: '', titleFontFamily: '',
    },
    emailBranding: {
      productName: r?.EmailBranding?.ProductName ?? r?.Branding?.ProductName ?? '',
      subjectPrefix: r?.EmailBranding?.SubjectPrefix ?? '',
      preheader: r?.EmailBranding?.Preheader ?? '',
      footerText: r?.EmailBranding?.FooterText ?? '',
      fromName: r?.EmailBranding?.FromName ?? '',
      fromAddress: r?.EmailBranding?.FromAddress ?? '',
      replyTo: r?.EmailBranding?.ReplyTo ?? '',
    },
    loginExperience: {
      internal: true,
      magicLink: appConfig.config.MagicLinkSelfService,
      providerIds: loginProviderOptions.value.map((p) => p.value),
    },
    selfReg: {
      posture: '',
      enabled: r?.SelfRegistration?.Enabled ?? false,
      requireEmailVerification: r?.SelfRegistration?.RequireEmailVerification ?? false,
      requireAdminApproval: r?.SelfRegistration?.RequireAdminApproval ?? false,
      allowedEmailDomains: r?.SelfRegistration?.AllowedEmailDomains ?? [],
      defaultGroupIds: r?.SelfRegistration?.DefaultGroupIds ?? [],
      termsOfServiceUrl: r?.SelfRegistration?.TermsOfServiceUrl ?? '',
      privacyPolicyUrl: r?.SelfRegistration?.PrivacyPolicyUrl ?? '',
    },
    registrationFields: {
      username: r?.RegistrationFields?.Username ?? '',
      firstname: r?.RegistrationFields?.Firstname ?? '',
      lastname: r?.RegistrationFields?.Lastname ?? '',
    },
    clientSessions: {
      idle: numStr(r?.ClientSessions?.IdleLifetimeDays),
      absolute: numStr(r?.ClientSessions?.AbsoluteLifetimeDays),
    },
    nativeGrants: {
      enabled: r?.NativeGrants?.Enabled ?? false,
      access: numStr(r?.NativeGrants?.AccessTokenLifetimeMinutes),
      refresh: numStr(r?.NativeGrants?.RefreshTokenLifetimeDays),
    },
    dcr: {
      enabled: r?.Dcr?.Enabled ?? false,
      access: numStr(r?.Dcr?.AccessTokenLifetimeMinutes),
      refresh: numStr(r?.Dcr?.RefreshTokenLifetimeDays),
      reservedNames: r?.Dcr?.ReservedNames ?? [],
      perIp: numStr(r?.Dcr?.PerIpRateLimitPerHour),
      perRealm: numStr(r?.Dcr?.PerRealmRateLimitPerDay),
    },
    cimd: {
      enabled: r?.Cimd?.Enabled ?? false,
      access: numStr(r?.Cimd?.AccessTokenLifetimeMinutes),
      refresh: numStr(r?.Cimd?.RefreshTokenLifetimeDays),
    },
  } as Record<string, Record<string, unknown>>
})

// Binds one field: shows the staged override value when the section overrides,
// otherwise the greyed realm default. Disabled (and left at the inherited display)
// until the section's "override" toggle is on.
// Returns `any` so the v-bind spread satisfies each component's differently-typed
// modelValue (string / boolean / string[]) without per-field casts.
function fieldBind(section: string, field: string): any {
  const s = (f as Record<string, Record<string, unknown>>)[section]!
  const realmVals = inh.value[section]!
  return {
    modelValue: s.override ? s[field] : realmVals[field],
    'onUpdate:modelValue': (v: unknown) => { s[field] = v },
    disabled: !s.override,
  }
}

function resetForm() {
  f.origin.override = false; f.origin.subdomain = ''
  f.branding.override = false; f.branding.productName = ''; f.branding.primaryColor = ''
  f.branding.logoAssetId = null; f.branding.faviconAssetId = null
  f.branding.logoUrl = null; f.branding.faviconUrl = null
  f.pageTheme.override = false; f.pageTheme.accentColor = ''; f.pageTheme.errorColor = ''
  f.pageTheme.buttonRadius = ''; f.pageTheme.inputRadius = ''; f.pageTheme.cardRadius = ''
  f.pageTheme.bodyFontFamily = ''; f.pageTheme.titleFontFamily = ''
  f.emailBranding.override = false; f.emailBranding.productName = ''
  f.emailBranding.subjectPrefix = ''; f.emailBranding.preheader = ''; f.emailBranding.footerText = ''
  f.emailBranding.fromName = ''; f.emailBranding.fromAddress = ''; f.emailBranding.replyTo = ''
  f.loginExperience.override = false; f.loginExperience.internal = true
  f.loginExperience.magicLink = true; f.loginExperience.providerIds = []
  f.selfReg.override = false; f.selfReg.posture = ''; f.selfReg.enabled = false
  f.selfReg.requireEmailVerification = true; f.selfReg.requireAdminApproval = false
  f.selfReg.allowedEmailDomains = []; f.selfReg.defaultGroupIds = []
  f.selfReg.termsOfServiceUrl = ''; f.selfReg.privacyPolicyUrl = ''
  f.registrationFields.override = false; f.registrationFields.username = ''
  f.registrationFields.firstname = ''; f.registrationFields.lastname = ''
  f.clientSessions.override = false; f.clientSessions.idle = ''; f.clientSessions.absolute = ''
  f.nativeGrants.override = false; f.nativeGrants.enabled = false; f.nativeGrants.access = ''; f.nativeGrants.refresh = ''
  f.rateLimits.override = false; f.rateLimits.overrides = emptyRateLimitOverrides()
  f.rateLimits.allowlistOverride = false; f.rateLimits.allowlist = []; f.rateLimits.mode = 'inherit'
  f.dcr.override = false; f.dcr.enabled = false; f.dcr.access = ''; f.dcr.refresh = ''
  f.dcr.reservedNames = []; f.dcr.perIp = ''; f.dcr.perRealm = ''
  f.cimd.override = false; f.cimd.enabled = false; f.cimd.access = ''; f.cimd.refresh = ''
  f.changeFeed.enabled = false; f.changeFeed.retentionAgeDays = 7; f.changeFeed.minimumEventCount = 1000
}

function populate(s?: ApplicationSettingsDto | null) {
  resetForm()
  if (!s) return
  if (s.Origin) { f.origin.override = true; f.origin.subdomain = s.Origin.Subdomain ?? '' }
  if (s.Branding) {
    f.branding.override = true
    f.branding.productName = s.Branding.ProductName ?? ''
    f.branding.primaryColor = s.Branding.PrimaryColor ?? ''
    f.branding.logoAssetId = s.Branding.LogoAssetId ?? null
    f.branding.faviconAssetId = s.Branding.FaviconAssetId ?? null
    f.branding.logoUrl = s.Branding.LogoUrl ?? null
    f.branding.faviconUrl = s.Branding.FaviconUrl ?? null
  }
  if (s.PageTheme) {
    f.pageTheme.override = true
    f.pageTheme.accentColor = s.PageTheme.AccentColor ?? ''
    f.pageTheme.errorColor = s.PageTheme.ErrorColor ?? ''
    f.pageTheme.buttonRadius = numStr(s.PageTheme.ButtonRadiusPx)
    f.pageTheme.inputRadius = numStr(s.PageTheme.InputRadiusPx)
    f.pageTheme.cardRadius = numStr(s.PageTheme.CardRadiusPx)
    f.pageTheme.bodyFontFamily = s.PageTheme.BodyFontFamily ?? ''
    f.pageTheme.titleFontFamily = s.PageTheme.TitleFontFamily ?? ''
  }
  if (s.EmailBranding) {
    f.emailBranding.override = true
    f.emailBranding.productName = s.EmailBranding.ProductName ?? ''
    f.emailBranding.subjectPrefix = s.EmailBranding.SubjectPrefix ?? ''
    f.emailBranding.preheader = s.EmailBranding.Preheader ?? ''
    f.emailBranding.footerText = s.EmailBranding.FooterText ?? ''
    f.emailBranding.fromName = s.EmailBranding.FromName ?? ''
    f.emailBranding.fromAddress = s.EmailBranding.FromAddress ?? ''
    f.emailBranding.replyTo = s.EmailBranding.ReplyTo ?? ''
  }
  if (s.LoginExperience) {
    f.loginExperience.override = true
    f.loginExperience.internal = s.LoginExperience.InternalLoginEnabled ?? true
    f.loginExperience.magicLink = s.LoginExperience.MagicLinkEnabled ?? true
    f.loginExperience.providerIds = s.LoginExperience.LoginProviderIds ?? []
  }
  if (s.SelfRegistration) {
    const sr = s.SelfRegistration
    f.selfReg.override = true
    f.selfReg.posture = (sr.Posture as typeof f.selfReg.posture) ?? ''
    f.selfReg.enabled = sr.Enabled ?? false
    f.selfReg.requireEmailVerification = sr.RequireEmailVerification ?? true
    f.selfReg.requireAdminApproval = sr.RequireAdminApproval ?? false
    f.selfReg.allowedEmailDomains = sr.AllowedEmailDomains ?? []
    f.selfReg.defaultGroupIds = sr.DefaultGroupIds ?? []
    f.selfReg.termsOfServiceUrl = sr.TermsOfServiceUrl ?? ''
    f.selfReg.privacyPolicyUrl = sr.PrivacyPolicyUrl ?? ''
  }
  if (s.RegistrationFields) {
    const rf = s.RegistrationFields
    f.registrationFields.override = true
    f.registrationFields.username = (rf.Username as typeof f.registrationFields.username) ?? ''
    f.registrationFields.firstname = (rf.Firstname as typeof f.registrationFields.firstname) ?? ''
    f.registrationFields.lastname = (rf.Lastname as typeof f.registrationFields.lastname) ?? ''
  }
  if (s.NativeGrants) {
    f.nativeGrants.override = true
    f.nativeGrants.enabled = s.NativeGrants.Enabled ?? false
    f.nativeGrants.access = numStr(s.NativeGrants.AccessTokenLifetimeMinutes)
    f.nativeGrants.refresh = numStr(s.NativeGrants.RefreshTokenLifetimeDays)
  }
  if (s.AuthRateLimits) {
    f.rateLimits.override = true
    f.rateLimits.overrides = overridesFromUpdate(s.AuthRateLimits)
    f.rateLimits.allowlistOverride = s.AuthRateLimits.SourceAllowlist != null
    f.rateLimits.allowlist = [...(s.AuthRateLimits.SourceAllowlist ?? [])]
    f.rateLimits.mode = s.AuthRateLimits.Mode ?? 'inherit'
  }
  if (s.ClientSessions) {
    f.clientSessions.override = true
    f.clientSessions.idle = numStr(s.ClientSessions.IdleLifetimeDays)
    f.clientSessions.absolute = numStr(s.ClientSessions.AbsoluteLifetimeDays)
  }
  if (s.Dcr) {
    f.dcr.override = true
    f.dcr.enabled = s.Dcr.Enabled ?? false
    f.dcr.access = numStr(s.Dcr.AccessTokenLifetimeMinutes)
    f.dcr.refresh = numStr(s.Dcr.RefreshTokenLifetimeDays)
    f.dcr.reservedNames = s.Dcr.ReservedNames ?? []
    f.dcr.perIp = numStr(s.Dcr.PerIpRateLimitPerHour)
    f.dcr.perRealm = numStr(s.Dcr.PerRealmRateLimitPerDay)
  }
  if (s.Cimd) {
    f.cimd.override = true
    f.cimd.enabled = s.Cimd.Enabled ?? false
    f.cimd.access = numStr(s.Cimd.AccessTokenLifetimeMinutes)
    f.cimd.refresh = numStr(s.Cimd.RefreshTokenLifetimeDays)
  }
  if (s.ChangeFeed) {
    f.changeFeed.enabled = s.ChangeFeed.Enabled
    f.changeFeed.retentionAgeDays = s.ChangeFeed.MinimumRetentionAgeDays ?? 7
    f.changeFeed.minimumEventCount = s.ChangeFeed.MinimumEventCount ?? 1000
  }
}

const orderedLoginProviders = computed(() => f.loginExperience.providerIds.map((id) => ({
  id,
  label: loginProviderOptions.value.find((option) => option.value === id)?.label ?? id,
})))

function moveLoginProvider(index: number, delta: -1 | 1) {
  if (!f.loginExperience.override) return
  const target = index + delta
  if (target < 0 || target >= f.loginExperience.providerIds.length) return
  const reordered = [...f.loginExperience.providerIds]
  ;[reordered[index], reordered[target]] = [reordered[target]!, reordered[index]!]
  f.loginExperience.providerIds = reordered
}

async function pickBrandAsset(kind: 'logo' | 'favicon') {
  if (!f.branding.override) return
  const currentId = kind === 'logo' ? f.branding.logoAssetId : f.branding.faviconAssetId
  const picker = dialog.open<AssetDto>(AssetPicker, {
    title: kind === 'logo'
      ? t('admin.customization.branding.pickLogo', {}, 'Select logo')
      : t('admin.customization.branding.pickFavicon', {}, 'Select favicon'),
    size: 'l',
  }, { selectedId: currentId })
  const selected = await picker.result
  if (!selected) return
  if (kind === 'logo') {
    f.branding.logoAssetId = selected.Id
    f.branding.logoUrl = selected.Url
  } else {
    f.branding.faviconAssetId = selected.Id
    f.branding.faviconUrl = selected.Url
  }
}

function clearBrandAsset(kind: 'logo' | 'favicon') {
  if (kind === 'logo') {
    f.branding.logoAssetId = null
    f.branding.logoUrl = null
  } else {
    f.branding.faviconAssetId = null
    f.branding.faviconUrl = null
  }
}

const effectiveLogoUrl = computed(() => f.branding.override
  ? f.branding.logoUrl || realmSettingsStore.settings?.Branding?.LogoUrl || null
  : realmSettingsStore.settings?.Branding?.LogoUrl ?? null)
const effectiveFaviconUrl = computed(() => f.branding.override
  ? f.branding.faviconUrl || realmSettingsStore.settings?.Branding?.FaviconUrl || null
  : realmSettingsStore.settings?.Branding?.FaviconUrl ?? null)
const effectiveProductName = computed(() => f.branding.override
  ? f.branding.productName.trim() || realmSettingsStore.settings?.Branding?.ProductName || ''
  : realmSettingsStore.settings?.Branding?.ProductName ?? '')
const effectivePrimaryColor = computed(() => f.branding.override
  ? f.branding.primaryColor.trim() || realmSettingsStore.settings?.Branding?.PrimaryColor || ''
  : realmSettingsStore.settings?.Branding?.PrimaryColor ?? '')
const realmEmail = computed(() => realmSettingsStore.settings?.EmailBranding)
const effectiveEmailProductName = computed(() => f.emailBranding.override
  ? f.emailBranding.productName.trim() || realmEmail.value?.ProductName || effectiveProductName.value
  : realmEmail.value?.ProductName || effectiveProductName.value)
const effectiveEmailSubjectPrefix = computed(() => f.emailBranding.override
  ? f.emailBranding.subjectPrefix.trim() || realmEmail.value?.SubjectPrefix || ''
  : realmEmail.value?.SubjectPrefix || '')
const effectiveEmailPreheader = computed(() => f.emailBranding.override
  ? f.emailBranding.preheader.trim() || realmEmail.value?.Preheader || ''
  : realmEmail.value?.Preheader || '')
const effectiveEmailFooterText = computed(() => f.emailBranding.override
  ? f.emailBranding.footerText.trim() || realmEmail.value?.FooterText || ''
  : realmEmail.value?.FooterText || '')

/** Build the override DTO as the COMPLETE desired state (the App PUT is a replace):
 * an overridden section sends its values, a non-overridden section sends `null` so the
 * backend clears that override (→ inherit the realm). Origin always sends a
 * section so turning the toggle off explicitly removes any existing route. */
function build(): ApplicationSettingsDto {
  return {
    Origin: { Subdomain: f.origin.override ? (f.origin.subdomain.trim() || null) : null },
    Branding: f.branding.override
      ? {
          ProductName: f.branding.productName.trim() || null,
          PrimaryColor: f.branding.primaryColor.trim() || null,
          LogoAssetId: f.branding.logoAssetId,
          FaviconAssetId: f.branding.faviconAssetId,
        }
      : null,
    PageTheme: f.pageTheme.override
      ? {
          AccentColor: f.pageTheme.accentColor.trim() || null,
          ErrorColor: f.pageTheme.errorColor.trim() || null,
          ButtonRadiusPx: parseNum(f.pageTheme.buttonRadius),
          InputRadiusPx: parseNum(f.pageTheme.inputRadius),
          CardRadiusPx: parseNum(f.pageTheme.cardRadius),
          BodyFontFamily: f.pageTheme.bodyFontFamily.trim() || null,
          TitleFontFamily: f.pageTheme.titleFontFamily.trim() || null,
        }
      : null,
    EmailBranding: f.emailBranding.override
      ? {
          ProductName: f.emailBranding.productName.trim() || null,
          SubjectPrefix: f.emailBranding.subjectPrefix.trim() || null,
          Preheader: f.emailBranding.preheader.trim() || null,
          FooterText: f.emailBranding.footerText.trim() || null,
          FromName: f.emailBranding.fromName.trim() || null,
          FromAddress: f.emailBranding.fromAddress.trim() || null,
          ReplyTo: f.emailBranding.replyTo.trim() || null,
        }
      : null,
    LoginExperience: f.loginExperience.override
      ? {
          InternalLoginEnabled: f.loginExperience.internal,
          MagicLinkEnabled: f.loginExperience.magicLink,
          LoginProviderIds: f.loginExperience.providerIds,
        }
      : null,
    SelfRegistration: f.selfReg.override
      ? {
          Posture: f.selfReg.posture || null,
          Enabled: f.selfReg.enabled,
          RequireEmailVerification: f.selfReg.requireEmailVerification,
          RequireAdminApproval: f.selfReg.requireAdminApproval,
          AllowedEmailDomains: f.selfReg.allowedEmailDomains.length ? f.selfReg.allowedEmailDomains : null,
          DefaultGroupIds: f.selfReg.defaultGroupIds.length ? f.selfReg.defaultGroupIds : null,
          TermsOfServiceUrl: f.selfReg.termsOfServiceUrl.trim() || null,
          PrivacyPolicyUrl: f.selfReg.privacyPolicyUrl.trim() || null,
        }
      : null,
    RegistrationFields: f.registrationFields.override
      ? {
          Username: f.registrationFields.username || null,
          Firstname: f.registrationFields.firstname || null,
          Lastname: f.registrationFields.lastname || null,
        }
      : null,
    NativeGrants: f.nativeGrants.override
      ? { Enabled: f.nativeGrants.enabled, AccessTokenLifetimeMinutes: parseNum(f.nativeGrants.access), RefreshTokenLifetimeDays: parseNum(f.nativeGrants.refresh) }
      : null,
    AuthRateLimits: f.rateLimits.override
      ? {
          Policies: sparseRateLimitPolicies(f.rateLimits.overrides),
          SourceAllowlist: f.rateLimits.allowlistOverride ? [...f.rateLimits.allowlist] : undefined,
          Mode: f.rateLimits.mode === 'inherit' ? undefined : f.rateLimits.mode,
        }
      : null,
    ClientSessions: f.clientSessions.override
      ? { IdleLifetimeDays: parseNum(f.clientSessions.idle), AbsoluteLifetimeDays: parseNum(f.clientSessions.absolute) }
      : null,
    Dcr: f.dcr.override
      ? {
          Enabled: f.dcr.enabled,
          AccessTokenLifetimeMinutes: parseNum(f.dcr.access),
          RefreshTokenLifetimeDays: parseNum(f.dcr.refresh),
          ReservedNames: f.dcr.reservedNames.length ? f.dcr.reservedNames : null,
          PerIpRateLimitPerHour: parseNum(f.dcr.perIp),
          PerRealmRateLimitPerDay: parseNum(f.dcr.perRealm),
        }
      : null,
    Cimd: f.cimd.override
      ? { Enabled: f.cimd.enabled, AccessTokenLifetimeMinutes: parseNum(f.cimd.access), RefreshTokenLifetimeDays: parseNum(f.cimd.refresh) }
      : null,
    ChangeFeed: {
      Enabled: f.changeFeed.enabled,
      MinimumRetentionAgeDays: f.changeFeed.retentionAgeDays ?? 7,
      MinimumEventCount: f.changeFeed.minimumEventCount ?? 1000,
    },
  }
}

watch(() => props.modelValue, (s) => populate(s), { immediate: true })
onMounted(async () => {
  await Promise.all([groupStore.initialize(), loginProviderStore.loadAll()])
  groupOptions.value = groupStore.groups.map((g) => ({ value: g.Id, label: g.Name }))
  loginProviderOptions.value = loginProviderStore.providers
    .filter((p) => !p.IsBuiltIn && p.Enabled && (p.Type === 'Oidc' || p.Type === 'Saml'))
    .map((p) => ({ value: p.Id, label: p.DisplayName }))
  if (!realmSettingsStore.loaded) await realmSettingsStore.load()
})

defineExpose({ build })

// ── Application page selection (ADR-0001): pick a realm variant per slot ──
const PAGE_SLOT_META = [
  { slug: 'login', label: t('admin.customization.pages.login.title', {}, 'Login') },
  { slug: 'password-forgot', label: t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password') },
  { slug: 'logout', label: t('admin.customization.pages.logout.title', {}, 'Logout') },
  { slug: 'consent', label: t('admin.customization.pages.consent.title', {}, 'Consent') },
]
const APP_BUILT_IN = '__builtin__'
const appSlots = reactive<Record<string, AppSlotDto>>({})
const pagesError = ref<string | null>(null)
const pagesBusy = ref(false)

function appSlotOf(slug: string): AppSlotDto {
  return appSlots[slug] ?? { Slug: slug, InheritActive: true, ActiveVariantId: null, AvailableVariants: [] }
}

function appPagesApi() {
  return props.applicationId ? useAppPagesApi(props.applicationId) : null
}

async function loadAppPages() {
  const client = appPagesApi()
  if (!client || !appConfig.config.Features.PageBuilder) return
  try {
    const { Slots } = await client.listSlots()
    const bySlug = new Map(Slots.map((s) => [s.Slug, s]))
    for (const m of PAGE_SLOT_META) {
      appSlots[m.slug] = bySlug.get(m.slug)
        ?? { Slug: m.slug, InheritActive: true, ActiveVariantId: null, AvailableVariants: [] }
    }
  } catch (e: any) { pagesError.value = e?.message ?? String(e) }
}

// Active dropdown value: 'inherit' | '__builtin__' | realm-variantId.
function appActiveValue(slot: AppSlotDto): string {
  if (slot.InheritActive) return 'inherit'
  return slot.ActiveVariantId ?? APP_BUILT_IN
}

function appActiveOptions(slot: AppSlotDto) {
  return [
    { value: 'inherit', label: t('admin.appSettings.pages.inheritRealm', {}, 'Inherit realm') },
    { value: APP_BUILT_IN, label: t('admin.customization.pages.builtin', {}, 'Built-in (default)') },
    ...slot.AvailableVariants.map((v) => ({ value: v.Id, label: v.Name })),
  ]
}

async function setAppActive(slug: string, value: string | null) {
  const client = appPagesApi()
  if (!client) return
  pagesBusy.value = true
  pagesError.value = null
  try {
    if (value === 'inherit' || value === null) await client.setActive(slug, true, null)
    else if (value === APP_BUILT_IN) await client.setActive(slug, false, null)
    else await client.setActive(slug, false, value)
    await loadAppPages()
  } catch (e: any) { pagesError.value = e?.message ?? String(e) } finally { pagesBusy.value = false }
}

watch(() => [activeTab.value, props.applicationId] as const, ([tab]) => {
  if (tab === 'pages') loadAppPages()
})
</script>

<template>
  <div class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
    <CoarTabGroup v-model="activeTab" class="tab-bar">
      <CoarTab id="origin">{{ t('admin.appSettings.tabs.origin', {}, 'Origin & Branding') }}</CoarTab>
      <CoarTab id="registration">{{ t('admin.appSettings.tabs.registration', {}, 'Registrierung') }}</CoarTab>
      <CoarTab id="sessions">{{ t('admin.appSettings.tabs.sessions', {}, 'Sessions') }}</CoarTab>
      <CoarTab id="grants">{{ t('admin.appSettings.tabs.grants', {}, 'Native Grants') }}</CoarTab>
      <CoarTab id="rateLimits">{{ t('admin.appSettings.tabs.rateLimits', {}, 'Rate limits') }}</CoarTab>
      <CoarTab id="oauth">{{ t('admin.appSettings.tabs.oauth', {}, 'OAuth (DCR/CIMD)') }}</CoarTab>
      <CoarTab id="sync">{{ t('admin.appSettings.tabs.sync', {}, 'Sync') }}</CoarTab>
      <CoarTab v-if="appConfig.config.Features.PageBuilder" id="pages">
        {{ t('admin.appSettings.tabs.pages', {}, 'Pages') }}
      </CoarTab>
    </CoarTabGroup>

    <!-- Origin & Branding -->
    <div v-show="activeTab === 'origin'" class="tab-content">
      <CoarCheckbox v-model="f.origin.override" :label="t('admin.appSettings.origin.override', {}, 'Dedicated subdomain for this app')" />
      <CoarFormField :label="t('admin.appSettings.origin.subdomain', {}, 'Subdomain (Child der Realm-Primary-Domain)')">
        <CoarTextInput v-bind="fieldBind('origin', 'subdomain')" clearable placeholder="amzettel.cocoar.app" />
      </CoarFormField>

      <CoarCheckbox v-model="f.branding.override" :label="t('admin.appSettings.branding.override', {}, 'Custom Branding (Login/SPA)')" />
      <CoarFormField :label="t('admin.appSettings.branding.productName', {}, 'Produktname')">
        <CoarTextInput v-bind="fieldBind('branding', 'productName')" clearable />
      </CoarFormField>
      <CoarFormField :label="t('admin.appSettings.branding.primaryColor', {}, 'Primary Color (CSS)')">
        <ColorField v-bind="fieldBind('branding', 'primaryColor')" placeholder="#1077be" />
      </CoarFormField>
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.customization.branding.logo', {}, 'Logo')">
          <div class="flex items-center gap-2">
            <div class="flex h-12 w-12 shrink-0 items-center justify-center overflow-hidden rounded border border-surface-200 bg-surface-50">
              <img v-if="effectiveLogoUrl" :src="effectiveLogoUrl" alt="" class="max-h-full max-w-full object-contain" />
              <span v-else class="text-xs text-surface-400">—</span>
            </div>
            <CoarButton size="s" variant="ghost" :disabled="!f.branding.override" @click="pickBrandAsset('logo')">
              {{ t('admin.customization.branding.pick', {}, 'Browse…') }}
            </CoarButton>
            <CoarButton v-if="f.branding.override && f.branding.logoAssetId" size="s" variant="ghost" @click="clearBrandAsset('logo')">
              {{ t('common.clear', {}, 'Clear') }}
            </CoarButton>
          </div>
        </CoarFormField>
        <CoarFormField :label="t('admin.customization.branding.favicon', {}, 'Favicon')">
          <div class="flex items-center gap-2">
            <div class="flex h-12 w-12 shrink-0 items-center justify-center overflow-hidden rounded border border-surface-200 bg-surface-50">
              <img v-if="effectiveFaviconUrl" :src="effectiveFaviconUrl" alt="" class="max-h-full max-w-full object-contain" />
              <span v-else class="text-xs text-surface-400">—</span>
            </div>
            <CoarButton size="s" variant="ghost" :disabled="!f.branding.override" @click="pickBrandAsset('favicon')">
              {{ t('admin.customization.branding.pick', {}, 'Browse…') }}
            </CoarButton>
            <CoarButton v-if="f.branding.override && f.branding.faviconAssetId" size="s" variant="ghost" @click="clearBrandAsset('favicon')">
              {{ t('common.clear', {}, 'Clear') }}
            </CoarButton>
          </div>
        </CoarFormField>
      </div>

      <CoarCheckbox
        v-if="appConfig.config.Features.PageBuilder"
        v-model="f.pageTheme.override"
        :label="t('admin.appSettings.pageTheme.override', {}, 'Custom page theme')" />
      <template v-if="appConfig.config.Features.PageBuilder">
        <CoarNotice variant="info">
          {{ t('admin.appSettings.pageTheme.scope', {}, 'These tokens apply only inside this application’s custom pages. Built-in pages and the Modgud administration UI are never affected.') }}
        </CoarNotice>
        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.appSettings.pageTheme.accentColor', {}, 'Accent color')">
            <ColorField v-bind="fieldBind('pageTheme', 'accentColor')" placeholder="#10b981" />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.pageTheme.errorColor', {}, 'Error color')">
            <ColorField v-bind="fieldBind('pageTheme', 'errorColor')" placeholder="#e5484d" />
          </CoarFormField>
        </div>
        <div class="grid grid-cols-3 gap-3">
          <CoarFormField :label="t('admin.appSettings.pageTheme.buttonRadius', {}, 'Button radius (px)')">
            <CoarTextInput v-bind="fieldBind('pageTheme', 'buttonRadius')" type="number" min="0" max="999" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.pageTheme.inputRadius', {}, 'Input radius (px)')">
            <CoarTextInput v-bind="fieldBind('pageTheme', 'inputRadius')" type="number" min="0" max="999" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.pageTheme.cardRadius', {}, 'Card radius (px)')">
            <CoarTextInput v-bind="fieldBind('pageTheme', 'cardRadius')" type="number" min="0" max="999" clearable />
          </CoarFormField>
        </div>
        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.appSettings.pageTheme.bodyFontFamily', {}, 'Body font family')">
            <CoarTextInput v-bind="fieldBind('pageTheme', 'bodyFontFamily')" placeholder="Instrument Sans Variable" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.appSettings.pageTheme.titleFontFamily', {}, 'Title font family')">
            <CoarTextInput v-bind="fieldBind('pageTheme', 'titleFontFamily')" placeholder="Instrument Sans Variable" clearable />
          </CoarFormField>
        </div>
      </template>

      <CoarCheckbox v-model="f.emailBranding.override" :label="t('admin.appSettings.email.override', {}, 'Custom Email Branding')" />
      <CoarFormField :label="t('admin.appSettings.email.fromName', {}, 'Sender display name')">
        <CoarTextInput v-bind="fieldBind('emailBranding', 'fromName')" clearable />
      </CoarFormField>
      <!-- Both fields in this row carry a hint glyph so their labels and inputs sit level. -->
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField
          :label="t('admin.appSettings.email.fromAddress', {}, 'Sender address')"
          :hint="t('admin.appSettings.email.fromAddressHint', {}, 'The address mail is sent from. Empty = inherit. Make sure your mail provider allows sending from it (SPF/DKIM).')"
        >
          <CoarTextInput v-bind="fieldBind('emailBranding', 'fromAddress')" type="email" clearable placeholder="noreply@example.com" />
        </CoarFormField>
        <CoarFormField
          :label="t('admin.appSettings.email.replyTo', {}, 'Reply-to address')"
          :hint="t('admin.appSettings.email.replyToHint', {}, 'Where replies to outbound mail go. Empty = replies go to the sender address.')"
        >
          <CoarTextInput v-bind="fieldBind('emailBranding', 'replyTo')" type="email" clearable />
        </CoarFormField>
      </div>
      <CoarFormField :label="t('admin.appSettings.email.productName', {}, 'Produktname in E-Mails')">
        <CoarTextInput v-bind="fieldBind('emailBranding', 'productName')" clearable />
      </CoarFormField>
      <CoarFormField :label="t('admin.appSettings.email.subjectPrefix', {}, 'Subject prefix')">
        <CoarTextInput v-bind="fieldBind('emailBranding', 'subjectPrefix')" clearable placeholder="My App" />
      </CoarFormField>
      <CoarFormField :label="t('admin.appSettings.email.preheader', {}, 'Preheader text')">
        <CoarTextInput v-bind="fieldBind('emailBranding', 'preheader')" clearable />
      </CoarFormField>
      <CoarFormField :label="t('admin.appSettings.email.footer', {}, 'Footer text')">
        <CoarTextInput v-bind="fieldBind('emailBranding', 'footerText')" clearable />
      </CoarFormField>

      <CoarCheckbox v-model="f.loginExperience.override" :label="t('admin.appSettings.login.override', {}, 'Custom login methods')" />
      <CoarCheckbox v-bind="fieldBind('loginExperience', 'internal')" :label="t('admin.appSettings.login.internal', {}, 'Username/password and passkeys')" />
      <CoarCheckbox v-bind="fieldBind('loginExperience', 'magicLink')" :label="t('admin.appSettings.login.magicLink', {}, 'Magic-link sign-in')" />
      <CoarFormField
        :label="t('admin.appSettings.login.providers', {}, 'External identity providers (selection order = button order)')"
        :hint="t('admin.appSettings.login.providersHint', {}, 'An empty selection disables external sign-in for this application.')">
        <CoarMultiSelect v-bind="fieldBind('loginExperience', 'providerIds')" :options="loginProviderOptions" searchable clearable />
        <div v-if="orderedLoginProviders.length" class="provider-order">
          <div v-for="(provider, index) in orderedLoginProviders" :key="provider.id" class="provider-order-row">
            <span>{{ index + 1 }}. {{ provider.label }}</span>
            <span class="provider-order-actions">
              <CoarButton size="s" variant="ghost" :disabled="!f.loginExperience.override || index === 0" @click="moveLoginProvider(index, -1)">↑</CoarButton>
              <CoarButton size="s" variant="ghost" :disabled="!f.loginExperience.override || index === orderedLoginProviders.length - 1" @click="moveLoginProvider(index, 1)">↓</CoarButton>
            </span>
          </div>
        </div>
      </CoarFormField>
      <CoarNotice
        v-if="f.loginExperience.override && !f.loginExperience.internal && !f.loginExperience.magicLink && f.loginExperience.providerIds.length === 0"
        variant="warning">
        {{ t('admin.appSettings.login.noneWarning', {}, 'No sign-in method remains enabled for this application.') }}
      </CoarNotice>

      <BrandingPreview
        :product-name="effectiveProductName"
        :email-product-name="effectiveEmailProductName"
        :email-subject-prefix="effectiveEmailSubjectPrefix"
        :email-preheader="effectiveEmailPreheader"
        :email-footer-text="effectiveEmailFooterText"
        :logo-url="effectiveLogoUrl"
        :primary-color="effectivePrimaryColor" />
    </div>

    <!-- Registration -->
    <div v-show="activeTab === 'registration'" class="tab-content">
      <CoarCheckbox v-model="f.selfReg.override" :label="t('admin.appSettings.selfReg.override', {}, 'Custom Registration Policy')" />
      <CoarFormField :label="t('admin.appSettings.selfReg.posture', {}, 'Posture (passwortlose Registrierung)')">
        <CoarSelect v-bind="fieldBind('selfReg', 'posture')" :options="postureOptions" />
      </CoarFormField>

        <div v-if="f.selfReg.posture === 'InviteCode'"
          class="rounded border border-amber-300 bg-amber-50 dark:border-amber-700/50 dark:bg-amber-900/20 p-3 text-sm space-y-1">
          <p class="font-medium">{{ t('admin.appSettings.posture.inviteCode.title', {}, 'Invite-code posture: how to hand out codes') }}</p>
          <p>{{ t('admin.appSettings.posture.inviteCode.ui', {}, 'Works immediately: mint codes in the admin under “Invite Codes” (sidebar, OAuth & Federation) for this app — no further setup required.') }}</p>
          <p>{{ t('admin.appSettings.posture.inviteCode.m2m', {}, 'For automatic minting by the backend app (M2M): create an OAuth scope “invite:write” bound to this app (App-ID set), and give a ServiceAccount a credential carrying that scope. The app then calls POST /api/app/{appId}/invite-codes with its client_credentials token.') }}</p>
          <p class="text-gray-500">{{ t('admin.appSettings.posture.inviteCode.redeem', {}, 'Redemption: the code travels on the native sign-up request (InviteCode field); unknown emails become users only with a valid, unused code. Existing confirmed users sign in normally (the code is ignored).') }}</p>
        </div>
        <CoarCheckbox v-bind="fieldBind('selfReg', 'enabled')" :label="t('admin.appSettings.selfReg.enabled', {}, 'Self-registration active')" />
        <CoarCheckbox v-bind="fieldBind('selfReg', 'requireEmailVerification')" :label="t('admin.appSettings.selfReg.verify', {}, 'Email verification required')" />
        <CoarCheckbox v-bind="fieldBind('selfReg', 'requireAdminApproval')" :label="t('admin.appSettings.selfReg.approval', {}, 'Admin approval required')" />
        <CoarFormField :label="t('admin.appSettings.selfReg.domains', {}, 'Allowed email domains (empty = all)')">
          <EditableStringList v-bind="fieldBind('selfReg', 'allowedEmailDomains')" />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.selfReg.defaultGroups', {}, 'Default Groups (auto-membership after verification)')">
          <CoarMultiSelect
            v-bind="fieldBind('selfReg', 'defaultGroupIds')"
            :options="groupOptions"
            searchable
            clearable
            :placeholder="t('admin.appSettings.selfReg.defaultGroups.placeholder', {}, 'Select groups…')" />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.selfReg.tos', {}, 'AGB-URL')">
          <CoarTextInput v-bind="fieldBind('selfReg', 'termsOfServiceUrl')" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.selfReg.privacy', {}, 'Datenschutz-URL')">
          <CoarTextInput v-bind="fieldBind('selfReg', 'privacyPolicyUrl')" clearable />
        </CoarFormField>

      <CoarCheckbox v-model="f.registrationFields.override" :label="t('admin.appSettings.regFields.override', {}, 'Custom Required Fields at Registration')" />
      <CoarFormField :label="t('admin.regFields.username', {}, 'Benutzername')"
        :hint="t('admin.appSettings.regFields.hint', {}, 'Which identity fields are required at account creation. Email is always required. Native clients must collect required fields.')">
        <CoarSelect v-bind="fieldBind('registrationFields', 'username')" :options="requirementOptions" />
      </CoarFormField>
      <CoarFormField :label="t('admin.regFields.firstname', {}, 'Vorname')">
        <CoarSelect v-bind="fieldBind('registrationFields', 'firstname')" :options="requirementOptions" />
      </CoarFormField>
      <CoarFormField :label="t('admin.regFields.lastname', {}, 'Nachname')">
        <CoarSelect v-bind="fieldBind('registrationFields', 'lastname')" :options="requirementOptions" />
      </CoarFormField>
    </div>

    <!-- Native app / OAuth client sessions -->
    <div v-show="activeTab === 'sessions'" class="tab-content">
      <CoarNotice truncate variant="info">
        {{ t('admin.appSettings.sessions.hintShort', {}, 'Override the realm default for this app\'s refresh-token sessions.') }}
        <template #details>
          {{ t('admin.appSettings.sessions.hint', {}, 'Override the realm default for refresh-token-backed sessions in this app. Individual OAuth clients can override this again. Access-token lifetime is configured separately and remains short.') }}
        </template>
      </CoarNotice>
      <CoarCheckbox
        v-model="f.clientSessions.override"
        :label="t('admin.appSettings.sessions.override', {}, 'Custom client-session policy')" />
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.appSettings.sessions.idle', {}, 'Idle lifetime (days, 1–3650)')">
          <CoarTextInput v-bind="fieldBind('clientSessions', 'idle')" clearable placeholder="30" />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.sessions.absolute', {}, 'Absolute lifetime (days, 1–3650)')">
          <CoarTextInput v-bind="fieldBind('clientSessions', 'absolute')" clearable placeholder="365" />
        </CoarFormField>
      </div>
    </div>

    <!-- Native Grants -->
    <div v-show="activeTab === 'grants'" class="tab-content">
      <CoarCheckbox v-model="f.nativeGrants.override" :label="t('admin.appSettings.grants.override', {}, 'Custom Native Grant Settings')" />
      <CoarCheckbox v-bind="fieldBind('nativeGrants', 'enabled')" :label="t('admin.appSettings.grants.enabled', {}, 'Native Grants active')" />
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.appSettings.access', {}, 'Access-Token (Min, 1–60)')">
          <CoarTextInput v-bind="fieldBind('nativeGrants', 'access')" clearable placeholder="15" />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.refresh', {}, 'Refresh-Token (Tage, 1–30)')">
          <CoarTextInput v-bind="fieldBind('nativeGrants', 'refresh')" clearable placeholder="14" />
        </CoarFormField>
      </div>
    </div>

    <!-- Rate limits (ADR 0007) -->
    <div v-show="activeTab === 'rateLimits'" class="tab-content">
      <CoarCheckbox v-model="f.rateLimits.override" :label="t('admin.appSettings.rateLimits.override', {}, 'Custom rate limits for this App')" />
      <p class="text-sm">{{ t('admin.appSettings.rateLimits.hint', {}, 'Only the cells you override win over the realm; everything else inherits. The allowlist and the enforcement mode replace the realm values when set.') }}</p>
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.rateLimits.mode.label', {}, 'Enforcement')">
          <CoarSelect v-model="f.rateLimits.mode" :options="appRateLimitModeOptions" :disabled="!f.rateLimits.override" />
        </CoarFormField>
        <CoarCheckbox v-model="f.rateLimits.allowlistOverride" :disabled="!f.rateLimits.override"
          :label="t('admin.appSettings.rateLimits.allowlistOverride', {}, 'Own source allowlist (replaces the realm list)')" />
      </div>
      <EditableStringList
        v-if="f.rateLimits.override && f.rateLimits.allowlistOverride"
        v-model="f.rateLimits.allowlist"
        appearance="compact-grid"
        min-height="8rem"
        :header-label="t('admin.rateLimits.allowlist', {}, 'Source allowlist — addresses or CIDR ranges exempt from the Source ceilings only (Target, Client and App still apply)')" />
      <AuthRateLimitsEditor v-model="f.rateLimits.overrides" :baseline="realmRateLimitBaseline" :disabled="!f.rateLimits.override" />
    </div>

    <!-- OAuth (DCR / CIMD) -->
    <div v-show="activeTab === 'oauth'" class="tab-content">
      <CoarCheckbox v-model="f.dcr.override" :label="t('admin.appSettings.dcr.override', {}, 'Custom DCR Settings')" />
      <CoarCheckbox v-bind="fieldBind('dcr', 'enabled')" :label="t('admin.appSettings.dcr.enabled', {}, 'Dynamic Client Registration active')" />
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.appSettings.access', {}, 'Access-Token (Min, 1–60)')">
          <CoarTextInput v-bind="fieldBind('dcr', 'access')" clearable placeholder="15" />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.refresh', {}, 'Refresh-Token (Tage, 1–30)')">
          <CoarTextInput v-bind="fieldBind('dcr', 'refresh')" clearable placeholder="7" />
        </CoarFormField>
      </div>
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.appSettings.dcr.perIp', {}, 'Rate-Limit pro IP / Stunde')">
          <CoarTextInput v-bind="fieldBind('dcr', 'perIp')" clearable placeholder="5" />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.dcr.perRealm', {}, 'Rate-Limit pro Realm / Tag')">
          <CoarTextInput v-bind="fieldBind('dcr', 'perRealm')" clearable placeholder="100" />
        </CoarFormField>
      </div>
      <CoarFormField :label="t('admin.appSettings.dcr.reservedNames', {}, 'Reservierte Client-Namen (Blockliste)')">
        <EditableStringList v-bind="fieldBind('dcr', 'reservedNames')" />
      </CoarFormField>

      <CoarCheckbox v-model="f.cimd.override" :label="t('admin.appSettings.cimd.override', {}, 'Custom CIMD Settings')" />
      <CoarCheckbox v-bind="fieldBind('cimd', 'enabled')" :label="t('admin.appSettings.cimd.enabled', {}, 'CIMD active')" />
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.appSettings.access', {}, 'Access-Token (Min, 1–60)')">
          <CoarTextInput v-bind="fieldBind('cimd', 'access')" clearable placeholder="15" />
        </CoarFormField>
        <CoarFormField :label="t('admin.appSettings.refresh', {}, 'Refresh-Token (Tage, 1–30)')">
          <CoarTextInput v-bind="fieldBind('cimd', 'refresh')" clearable placeholder="7" />
        </CoarFormField>
      </div>
    </div>

    <!-- Consumer change feed -->
    <div v-show="activeTab === 'sync'" class="tab-content">
      <CoarNotice truncate variant="info">
        {{ t('admin.appSettings.changeFeed.hintShort', {}, 'Expose this app\'s current scope through a resumable consumer change feed.') }}
        <template #details>
          {{ t('admin.appSettings.changeFeed.hint', {}, 'Authorized OAuth clients assigned to this application can take a full snapshot and then resume changes through SSE or the polling endpoint. The feed contains a short-lived integration projection, not raw event-store events.') }}
        </template>
      </CoarNotice>
      <CoarCheckbox
        v-model="f.changeFeed.enabled"
        :label="t('admin.appSettings.changeFeed.enabled', {}, 'Enable consumer change feed')" />
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField
          :label="t('admin.appSettings.changeFeed.retentionAge', {}, 'Minimum retention age (days)')"
          :hint="t('admin.appSettings.changeFeed.retentionAgeHint', {}, 'Keep all changes inside this age window.')">
          <CoarNumberInput
            v-model="f.changeFeed.retentionAgeDays"
            :min="1"
            :max="3650"
            :step="1"
            placeholder="7" />
        </CoarFormField>
        <CoarFormField
          :label="t('admin.appSettings.changeFeed.minimumEvents', {}, 'Minimum retained changes')"
          :hint="t('admin.appSettings.changeFeed.minimumEventsHint', {}, 'Also keep at least this many newest changes, even for quiet applications.')">
          <CoarNumberInput
            v-model="f.changeFeed.minimumEventCount"
            :min="1"
            :max="1000000"
            :step="1"
            placeholder="1000" />
        </CoarFormField>
      </div>
    </div>

    <!-- PageBuilder schemas live behind dedicated endpoints so regular settings
         saves cannot accidentally overwrite a large page tree. -->
    <div v-if="appConfig.config.Features.PageBuilder" v-show="activeTab === 'pages'" class="tab-content">
      <CoarNotice v-if="!applicationId" variant="info">
        {{ t('admin.appSettings.pages.saveFirst', {}, 'Save the application first, then you can give its authentication pages their own layout.') }}
      </CoarNotice>
      <template v-else>
        <CoarNotice truncate variant="info">
          {{ t('admin.appSettings.pages.hintV3Short', {}, 'Pick which page variant this app uses per slot; inherit follows the realm.') }}
          <template #details>
            {{ t('admin.appSettings.pages.hintV3', {}, 'Pick which authentication page this application uses. Inherit follows the realm; variants are authored in Platform → Pages.') }}
          </template>
        </CoarNotice>
        <CoarNotice v-if="pagesError" variant="error">{{ pagesError }}</CoarNotice>

        <CoarFormField v-for="m in PAGE_SLOT_META" :key="m.slug" :label="m.label">
          <CoarSelect
            :model-value="appActiveValue(appSlotOf(m.slug))"
            :options="appActiveOptions(appSlotOf(m.slug))"
            :disabled="pagesBusy"
            @update:model-value="(v: string | null) => setAppActive(m.slug, v)" />
        </CoarFormField>
      </template>
    </div>
  </div>
</template>

<style scoped>
.tab-bar { margin-bottom: 8px; }
.tab-content { display: flex; flex-direction: column; gap: 12px; min-height: 0; }
.page-links { display: flex; flex-direction: column; gap: 8px; }
.page-link {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 12px;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: var(--coar-radius-m, 6px);
}
.page-link p {
  margin: 2px 0 0;
  color: var(--coar-text-neutral-secondary);
  font-size: 0.8rem;
}

.app-page-slots { display: flex; flex-direction: column; gap: 12px; }
.app-slot {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 12px;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: var(--coar-radius-m, 6px);
}
.app-slot-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}
.app-slot-active { display: flex; flex-direction: column; gap: 2px; min-width: 200px; }
.active-label {
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.03em;
  color: var(--coar-text-neutral-secondary);
}
.app-variants { display: flex; flex-direction: column; gap: 6px; }
.app-variant {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 6px 8px;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: 4px;
  background: var(--coar-background-neutral-primary);
}
.app-variant-active { border-color: var(--coar-text-accent-primary, #4f46e5); }
.app-variant-name { font-size: 0.85rem; font-weight: 500; }
.app-variant-actions { display: flex; gap: 4px; flex-shrink: 0; }
.provider-order { display: flex; flex-direction: column; gap: 4px; margin-top: 8px; }
.provider-order-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 8px;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: 4px;
  font-size: 0.85rem;
}
.provider-order-actions { display: flex; gap: 2px; }
</style>
