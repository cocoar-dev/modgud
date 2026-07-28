<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  CoarCard,
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarButton,
  CoarMultiSelect,
  CoarSelect,
  CoarTabGroup,
  CoarTab,
  CoarPopconfirm,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import EditableStringList from '@/components/EditableStringList.vue'
import Notice from '@/components/Notice.vue'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import { useGroupStore } from '@/stores/group.store'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useRealmPagesApi, type RealmSlotDto } from '@/composables/usePagesApi'
import type {
  SelfRegistrationDto,
  UpdateSelfRegistrationDto,
  DcrSettingsDto,
  UpdateDcrSettingsDto,
  CimdSettingsDto,
  UpdateCimdSettingsDto,
  NativeGrantSettingsDto,
  UpdateNativeGrantSettingsDto,
  BrowserSessionPolicyDto,
  UpdateBrowserSessionPolicyDto,
  ClientSessionPolicyDto,
  UpdateClientSessionPolicyDto,
  AuthRateLimitsDto,
  UpdateAuthRateLimitsDto,
  DeletionSettingsDto,
  UpdateDeletionSettingsDto,
  AuditSettingsDto,
  UpdateAuditSettingsDto,
  RegistrationFieldsSettingsDto,
  UpdateRegistrationFieldsSettingsDto,
  FieldRequirement,
} from '@/models/realmSettings'

const { t, language } = useI18n()
const ui = useUI()
const settingsStore = useRealmSettingsStore()
const groupStore = useGroupStore()
const authStore = useAuthStore()
const toast = useToast()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.realmSettings.title', {}, 'Realm settings')
  ctx.header.icon = 'sliders-horizontal'
  ctx.content.container = false
  ctx.content.hasSubNav = true
}), { immediate: true })

type TabId = 'self-registration' | 'registration-fields' | 'sessions' | 'dcr' | 'cimd' | 'native-grants' | 'auth-rate-limits' | 'audit' | 'deletion' | 'signing-keys' | 'pages'
const activeTab = ref<TabId>('self-registration')

const canRotateSigningKey = computed(() => authStore.hasPermission('realm-settings:write'))
const rotating = ref(false)

// ── PageBuilder: pick the active page variant per slot (ADR-0001) ──
const appConfig = useAppConfigStore()
const pageBuilderOn = computed(() => appConfig.config.Features.PageBuilder)
const pagesApi = useRealmPagesApi()
const PAGE_BUILT_IN = '__builtin__'
const PAGE_SLOT_META = [
  { slug: 'login', label: t('admin.customization.pages.login.title', {}, 'Login') },
  { slug: 'logout', label: t('admin.customization.pages.logout.title', {}, 'Logout') },
  { slug: 'password-forgot', label: t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password') },
]
const pageSlots = ref<Record<string, RealmSlotDto>>({})
const pagesError = ref<string | null>(null)
const pagesBusy = ref(false)

function pageSlotOf(slug: string): RealmSlotDto {
  return pageSlots.value[slug] ?? { Slug: slug, ActiveVariantId: null, Variants: [] }
}

function pageActiveOptions(slot: RealmSlotDto) {
  return [
    { value: PAGE_BUILT_IN, label: t('admin.customization.pages.builtin', {}, 'Built-in (default)') },
    ...slot.Variants.map((v) => ({ value: v.Id, label: v.Name })),
  ]
}

async function loadRealmPages() {
  if (!pageBuilderOn.value) return
  try {
    const { Slots } = await pagesApi.listSlots()
    const bySlug = new Map((Slots as RealmSlotDto[]).map((s) => [s.Slug, s]))
    const next: Record<string, RealmSlotDto> = {}
    for (const m of PAGE_SLOT_META) {
      next[m.slug] = bySlug.get(m.slug) ?? { Slug: m.slug, ActiveVariantId: null, Variants: [] }
    }
    pageSlots.value = next
  } catch (e: any) { pagesError.value = e?.message ?? String(e) }
}

async function setRealmPageActive(slug: string, value: string | null) {
  pagesBusy.value = true
  pagesError.value = null
  try {
    await pagesApi.setActive(slug, (value === null || value === PAGE_BUILT_IN) ? null : value)
    await loadRealmPages()
  } catch (e: any) { pagesError.value = e?.message ?? String(e) } finally { pagesBusy.value = false }
}

watch(activeTab, (tab) => { if (tab === 'pages') loadRealmPages() })

// ── Self-Registration form state ─────────────────────────────────────
interface SelfRegFormState {
  Enabled: boolean
  RequireEmailVerification: boolean
  RequireAdminApproval: boolean
  AllowedEmailDomains: string[]
  DefaultGroupIds: string[]
  TermsOfServiceUrl: string
  PrivacyPolicyUrl: string
  CaptchaEnabled: boolean
  CaptchaSiteKey: string
  CaptchaSecretSet: boolean
}

function emptySelfReg(): SelfRegFormState {
  return {
    Enabled: false,
    RequireEmailVerification: true,
    RequireAdminApproval: false,
    AllowedEmailDomains: [],
    DefaultGroupIds: [],
    TermsOfServiceUrl: '',
    PrivacyPolicyUrl: '',
    CaptchaEnabled: false,
    CaptchaSiteKey: '',
    CaptchaSecretSet: false,
  }
}

const form = ref<SelfRegFormState>(emptySelfReg())
const originalSelfReg = ref<SelfRegistrationDto | null>(null)

// ── DCR form state ───────────────────────────────────────────────────
interface DcrFormState {
  Enabled: boolean
  AccessTokenLifetimeMinutes: number
  RefreshTokenLifetimeDays: number
  GcTtlDays: number
  PerIpRateLimitPerHour: number
  PerRealmRateLimitPerDay: number
  ReservedNames: string[]
}

function emptyDcr(): DcrFormState {
  return {
    Enabled: false,
    AccessTokenLifetimeMinutes: 15,
    RefreshTokenLifetimeDays: 7,
    GcTtlDays: 90,
    PerIpRateLimitPerHour: 5,
    PerRealmRateLimitPerDay: 100,
    ReservedNames: [],
  }
}

const dcrForm = ref<DcrFormState>(emptyDcr())
const originalDcr = ref<DcrSettingsDto | null>(null)

// ── CIMD form state ──────────────────────────────────────────────────
interface CimdFormState {
  Enabled: boolean
  AccessTokenLifetimeMinutes: number
  RefreshTokenLifetimeDays: number
}

function emptyCimd(): CimdFormState {
  return { Enabled: false, AccessTokenLifetimeMinutes: 15, RefreshTokenLifetimeDays: 7 }
}

const cimdForm = ref<CimdFormState>(emptyCimd())
const originalCimd = ref<CimdSettingsDto | null>(null)

function cimdFromDto(d: CimdSettingsDto): CimdFormState {
  return {
    Enabled: d.Enabled,
    AccessTokenLifetimeMinutes: d.AccessTokenLifetimeMinutes,
    RefreshTokenLifetimeDays: d.RefreshTokenLifetimeDays,
  }
}

// ── Native-grants form state (ADR-0010) ──────────────────────────────
interface NativeGrantFormState {
  Enabled: boolean
  AccessTokenLifetimeMinutes: number
  RefreshTokenLifetimeDays: number
}

function emptyNativeGrants(): NativeGrantFormState {
  return { Enabled: false, AccessTokenLifetimeMinutes: 15, RefreshTokenLifetimeDays: 14 }
}

const nativeGrantsForm = ref<NativeGrantFormState>(emptyNativeGrants())
const originalNativeGrants = ref<NativeGrantSettingsDto | null>(null)

function nativeGrantsFromDto(d: NativeGrantSettingsDto): NativeGrantFormState {
  return {
    Enabled: d.Enabled,
    AccessTokenLifetimeMinutes: d.AccessTokenLifetimeMinutes,
    RefreshTokenLifetimeDays: d.RefreshTokenLifetimeDays,
  }
}

// ── Authoritative browser + native-client session policies ───────────
const browserSessionsForm = ref<BrowserSessionPolicyDto>({
  IdleLifetimeMinutes: 30 * 24 * 60,
  AbsoluteLifetimeMinutes: 180 * 24 * 60,
  AllowRememberMe: true,
})
const originalBrowserSessions = ref<BrowserSessionPolicyDto | null>(null)

const clientSessionsForm = ref<ClientSessionPolicyDto>({
  IdleLifetimeDays: 30,
  AbsoluteLifetimeDays: 365,
})
const originalClientSessions = ref<ClientSessionPolicyDto | null>(null)

// ── Auth rate-limit form state (per-IP ceilings, configurable per realm) ──
type RateLimitPolicyKey =
  'NativeOtp' | 'MagicLink' | 'PasswordReset' | 'EmailOtp'
  | 'EmailVerification' | 'PasskeyBegin' | 'Bootstrap'

type AuthRateLimitsFormState = Record<RateLimitPolicyKey, { PermitLimit: number; WindowMinutes: number }>

// Display order + labels for the rate-limit grid. Labels carry the endpoint so an
// admin knows which flow each ceiling gates.
const rateLimitPolicies: { key: RateLimitPolicyKey; labelKey: string; fallback: string }[] = [
  { key: 'NativeOtp', labelKey: 'admin.realmSettings.authRateLimits.nativeOtp', fallback: 'Native OTP request (passwordless login code)' },
  { key: 'MagicLink', labelKey: 'admin.realmSettings.authRateLimits.magicLink', fallback: 'Magic-link request' },
  { key: 'PasswordReset', labelKey: 'admin.realmSettings.authRateLimits.passwordReset', fallback: 'Password-reset request' },
  { key: 'EmailOtp', labelKey: 'admin.realmSettings.authRateLimits.emailOtp', fallback: 'Email-OTP login verify' },
  { key: 'EmailVerification', labelKey: 'admin.realmSettings.authRateLimits.emailVerification', fallback: 'Email verification resend' },
  { key: 'PasskeyBegin', labelKey: 'admin.realmSettings.authRateLimits.passkeyBegin', fallback: 'Passkey ceremony begin / enroll' },
  { key: 'Bootstrap', labelKey: 'admin.realmSettings.authRateLimits.bootstrap', fallback: 'First-admin bootstrap' },
]

function emptyAuthRateLimits(): AuthRateLimitsFormState {
  return {
    NativeOtp: { PermitLimit: 5, WindowMinutes: 60 },
    MagicLink: { PermitLimit: 5, WindowMinutes: 60 },
    PasswordReset: { PermitLimit: 5, WindowMinutes: 60 },
    EmailOtp: { PermitLimit: 30, WindowMinutes: 1 },
    EmailVerification: { PermitLimit: 5, WindowMinutes: 60 },
    PasskeyBegin: { PermitLimit: 60, WindowMinutes: 5 },
    Bootstrap: { PermitLimit: 10, WindowMinutes: 15 },
  }
}

const authRateLimitsForm = ref<AuthRateLimitsFormState>(emptyAuthRateLimits())
const originalAuthRateLimits = ref<AuthRateLimitsDto | null>(null)

function authRateLimitsFromDto(d: AuthRateLimitsDto): AuthRateLimitsFormState {
  const copy = (r: { PermitLimit: number; WindowMinutes: number }) =>
    ({ PermitLimit: r.PermitLimit, WindowMinutes: r.WindowMinutes })
  return {
    NativeOtp: copy(d.NativeOtp),
    MagicLink: copy(d.MagicLink),
    PasswordReset: copy(d.PasswordReset),
    EmailOtp: copy(d.EmailOtp),
    EmailVerification: copy(d.EmailVerification),
    PasskeyBegin: copy(d.PasskeyBegin),
    Bootstrap: copy(d.Bootstrap),
  }
}

// ── Deletion-policy form state ───────────────────────────────────────
interface DeletionFormState {
  GraceDays: number
  ReminderLeadDays: number
  AdminRetentionDays: number
  AutoPurgeEnabled: boolean
}

function emptyDeletion(): DeletionFormState {
  return { GraceDays: 30, ReminderLeadDays: 2, AdminRetentionDays: 30, AutoPurgeEnabled: true }
}

const deletionForm = ref<DeletionFormState>(emptyDeletion())
const originalDeletion = ref<DeletionSettingsDto | null>(null)

const auditForm = ref<AuditSettingsDto>({
  VisibilityWindowDays: 90,
  SecurityRetentionDays: 7,
})
const originalAudit = ref<AuditSettingsDto | null>(null)

function deletionFromDto(d: DeletionSettingsDto): DeletionFormState {
  return {
    GraceDays: d.GraceDays,
    ReminderLeadDays: d.ReminderLeadDays,
    AdminRetentionDays: d.AdminRetentionDays,
    AutoPurgeEnabled: d.AutoPurgeEnabled,
  }
}

// ── Registration-fields policy form state ────────────────────────────
const regFieldsForm = ref<RegistrationFieldsSettingsDto>({
  Username: 'Optional', Firstname: 'Optional', Lastname: 'Optional',
})
const originalRegFields = ref<RegistrationFieldsSettingsDto | null>(null)

function regFieldsFromDto(d: RegistrationFieldsSettingsDto): RegistrationFieldsSettingsDto {
  return { Username: d.Username, Firstname: d.Firstname, Lastname: d.Lastname }
}

const requirementOptions: { value: FieldRequirement; label: string }[] = [
  { value: 'Off', label: t('admin.regFields.off', {}, 'Aus') },
  { value: 'Optional', label: t('admin.regFields.optional', {}, 'Optional') },
  { value: 'Required', label: t('admin.regFields.required', {}, 'Required') },
]

function dcrFromDto(d: DcrSettingsDto): DcrFormState {
  return {
    Enabled: d.Enabled,
    AccessTokenLifetimeMinutes: d.AccessTokenLifetimeMinutes,
    RefreshTokenLifetimeDays: d.RefreshTokenLifetimeDays,
    GcTtlDays: d.GcTtlDays,
    PerIpRateLimitPerHour: d.PerIpRateLimitPerHour,
    PerRealmRateLimitPerDay: d.PerRealmRateLimitPerDay,
    ReservedNames: [...(d.ReservedNames ?? [])],
  }
}


// Captcha-secret — write-only with three states: leave, clear, replace.
const editingSecret = ref(false)
const secretInput = ref('')

const groupOptions = computed(() =>
  groupStore.groups.map((g) => ({ value: g.Id, label: g.Name })))

const initialLoad = ref(true)
const saving = ref(false)
const error = ref<string | null>(null)
const savedFlash = ref(false)

function fromDto(sr: SelfRegistrationDto): SelfRegFormState {
  return {
    Enabled: sr.Enabled,
    RequireEmailVerification: sr.RequireEmailVerification,
    RequireAdminApproval: sr.RequireAdminApproval,
    AllowedEmailDomains: [...(sr.AllowedEmailDomains ?? [])],
    DefaultGroupIds: [...(sr.DefaultGroupIds ?? [])],
    TermsOfServiceUrl: sr.TermsOfServiceUrl ?? '',
    PrivacyPolicyUrl: sr.PrivacyPolicyUrl ?? '',
    CaptchaEnabled: sr.CaptchaEnabled,
    CaptchaSiteKey: sr.CaptchaSiteKey ?? '',
    CaptchaSecretSet: sr.CaptchaSecretSet,
  }
}

onMounted(async () => {
  initialLoad.value = true
  try {
    const [dto] = await Promise.all([settingsStore.load(), groupStore.initialize()])
    originalSelfReg.value = dto.SelfRegistration
    form.value = fromDto(dto.SelfRegistration)
    originalDcr.value = dto.Dcr
    dcrForm.value = dcrFromDto(dto.Dcr)
    originalCimd.value = dto.Cimd
    cimdForm.value = cimdFromDto(dto.Cimd)
    originalNativeGrants.value = dto.NativeGrants
    nativeGrantsForm.value = nativeGrantsFromDto(dto.NativeGrants)
    originalBrowserSessions.value = dto.BrowserSessions
    browserSessionsForm.value = { ...dto.BrowserSessions }
    originalClientSessions.value = dto.ClientSessions
    clientSessionsForm.value = { ...dto.ClientSessions }
    originalAuthRateLimits.value = dto.AuthRateLimits
    authRateLimitsForm.value = authRateLimitsFromDto(dto.AuthRateLimits)
    originalDeletion.value = dto.Deletion
    deletionForm.value = deletionFromDto(dto.Deletion)
    originalAudit.value = dto.Audit
    auditForm.value = { ...dto.Audit }
    originalRegFields.value = dto.RegistrationFields
    regFieldsForm.value = regFieldsFromDto(dto.RegistrationFields)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.message ?? String(e)
  } finally {
    initialLoad.value = false
  }
})

function buildSelfRegPatch(): UpdateSelfRegistrationDto | undefined {
  const orig = originalSelfReg.value
  if (!orig) return undefined
  const cur = form.value
  const patch: UpdateSelfRegistrationDto = {}

  if (cur.Enabled !== orig.Enabled) patch.Enabled = cur.Enabled
  if (cur.RequireEmailVerification !== orig.RequireEmailVerification)
    patch.RequireEmailVerification = cur.RequireEmailVerification
  if (cur.RequireAdminApproval !== orig.RequireAdminApproval)
    patch.RequireAdminApproval = cur.RequireAdminApproval

  if (!arrayEqual(cur.AllowedEmailDomains, orig.AllowedEmailDomains ?? []))
    patch.AllowedEmailDomains = cur.AllowedEmailDomains.length ? cur.AllowedEmailDomains : null
  if (!arrayEqual(cur.DefaultGroupIds, orig.DefaultGroupIds ?? []))
    patch.DefaultGroupIds = cur.DefaultGroupIds.length ? cur.DefaultGroupIds : null

  const tos = cur.TermsOfServiceUrl.trim()
  if (tos !== (orig.TermsOfServiceUrl ?? '')) patch.TermsOfServiceUrl = tos || null
  const pp = cur.PrivacyPolicyUrl.trim()
  if (pp !== (orig.PrivacyPolicyUrl ?? '')) patch.PrivacyPolicyUrl = pp || null

  if (cur.CaptchaEnabled !== orig.CaptchaEnabled) patch.CaptchaEnabled = cur.CaptchaEnabled
  const key = cur.CaptchaSiteKey.trim()
  if (key !== (orig.CaptchaSiteKey ?? '')) patch.CaptchaSiteKey = key || null

  if (editingSecret.value) patch.CaptchaSecret = secretInput.value

  return Object.keys(patch).length === 0 ? undefined : patch
}

function arrayEqual(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false
  return true
}

function buildDcrPatch(): UpdateDcrSettingsDto | undefined {
  const orig = originalDcr.value
  if (!orig) return undefined
  const cur = dcrForm.value
  const patch: UpdateDcrSettingsDto = {}

  if (cur.Enabled !== orig.Enabled) patch.Enabled = cur.Enabled
  if (cur.AccessTokenLifetimeMinutes !== orig.AccessTokenLifetimeMinutes)
    patch.AccessTokenLifetimeMinutes = cur.AccessTokenLifetimeMinutes
  if (cur.RefreshTokenLifetimeDays !== orig.RefreshTokenLifetimeDays)
    patch.RefreshTokenLifetimeDays = cur.RefreshTokenLifetimeDays
  if (cur.GcTtlDays !== orig.GcTtlDays) patch.GcTtlDays = cur.GcTtlDays
  if (cur.PerIpRateLimitPerHour !== orig.PerIpRateLimitPerHour)
    patch.PerIpRateLimitPerHour = cur.PerIpRateLimitPerHour
  if (cur.PerRealmRateLimitPerDay !== orig.PerRealmRateLimitPerDay)
    patch.PerRealmRateLimitPerDay = cur.PerRealmRateLimitPerDay
  if (!arrayEqual(cur.ReservedNames, orig.ReservedNames ?? []))
    patch.ReservedNames = cur.ReservedNames.length ? cur.ReservedNames : null

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildCimdPatch(): UpdateCimdSettingsDto | undefined {
  const orig = originalCimd.value
  if (!orig) return undefined
  const cur = cimdForm.value
  const patch: UpdateCimdSettingsDto = {}

  if (cur.Enabled !== orig.Enabled) patch.Enabled = cur.Enabled
  if (cur.AccessTokenLifetimeMinutes !== orig.AccessTokenLifetimeMinutes)
    patch.AccessTokenLifetimeMinutes = cur.AccessTokenLifetimeMinutes
  if (cur.RefreshTokenLifetimeDays !== orig.RefreshTokenLifetimeDays)
    patch.RefreshTokenLifetimeDays = cur.RefreshTokenLifetimeDays

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildNativeGrantsPatch(): UpdateNativeGrantSettingsDto | undefined {
  const orig = originalNativeGrants.value
  if (!orig) return undefined
  const cur = nativeGrantsForm.value
  const patch: UpdateNativeGrantSettingsDto = {}

  if (cur.Enabled !== orig.Enabled) patch.Enabled = cur.Enabled
  if (cur.AccessTokenLifetimeMinutes !== orig.AccessTokenLifetimeMinutes)
    patch.AccessTokenLifetimeMinutes = cur.AccessTokenLifetimeMinutes
  if (cur.RefreshTokenLifetimeDays !== orig.RefreshTokenLifetimeDays)
    patch.RefreshTokenLifetimeDays = cur.RefreshTokenLifetimeDays

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildBrowserSessionsPatch(): UpdateBrowserSessionPolicyDto | undefined {
  const orig = originalBrowserSessions.value
  if (!orig) return undefined
  const cur = browserSessionsForm.value
  const patch: UpdateBrowserSessionPolicyDto = {}

  if (cur.IdleLifetimeMinutes !== orig.IdleLifetimeMinutes)
    patch.IdleLifetimeMinutes = cur.IdleLifetimeMinutes
  if (cur.AbsoluteLifetimeMinutes !== orig.AbsoluteLifetimeMinutes)
    patch.AbsoluteLifetimeMinutes = cur.AbsoluteLifetimeMinutes
  if (cur.AllowRememberMe !== orig.AllowRememberMe)
    patch.AllowRememberMe = cur.AllowRememberMe

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildClientSessionsPatch(): UpdateClientSessionPolicyDto | undefined {
  const orig = originalClientSessions.value
  if (!orig) return undefined
  const cur = clientSessionsForm.value
  const patch: UpdateClientSessionPolicyDto = {}

  if (cur.IdleLifetimeDays !== orig.IdleLifetimeDays)
    patch.IdleLifetimeDays = cur.IdleLifetimeDays
  if (cur.AbsoluteLifetimeDays !== orig.AbsoluteLifetimeDays)
    patch.AbsoluteLifetimeDays = cur.AbsoluteLifetimeDays

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildAuthRateLimitsPatch(): UpdateAuthRateLimitsDto | undefined {
  const orig = originalAuthRateLimits.value
  if (!orig) return undefined
  const cur = authRateLimitsForm.value
  const patch: UpdateAuthRateLimitsDto = {}

  for (const { key } of rateLimitPolicies) {
    const o = orig[key]
    const c = cur[key]
    if (c.PermitLimit !== o.PermitLimit || c.WindowMinutes !== o.WindowMinutes)
      patch[key] = { PermitLimit: c.PermitLimit, WindowMinutes: c.WindowMinutes }
  }

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildDeletionPatch(): UpdateDeletionSettingsDto | undefined {
  const orig = originalDeletion.value
  if (!orig) return undefined
  const cur = deletionForm.value
  const patch: UpdateDeletionSettingsDto = {}

  if (cur.GraceDays !== orig.GraceDays) patch.GraceDays = cur.GraceDays
  if (cur.ReminderLeadDays !== orig.ReminderLeadDays) patch.ReminderLeadDays = cur.ReminderLeadDays
  if (cur.AdminRetentionDays !== orig.AdminRetentionDays) patch.AdminRetentionDays = cur.AdminRetentionDays
  if (cur.AutoPurgeEnabled !== orig.AutoPurgeEnabled) patch.AutoPurgeEnabled = cur.AutoPurgeEnabled

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildAuditPatch(): UpdateAuditSettingsDto | undefined {
  const orig = originalAudit.value
  if (!orig) return undefined
  const patch: UpdateAuditSettingsDto = {}
  if (auditForm.value.VisibilityWindowDays !== orig.VisibilityWindowDays)
    patch.VisibilityWindowDays = auditForm.value.VisibilityWindowDays
  if (auditForm.value.SecurityRetentionDays !== orig.SecurityRetentionDays)
    patch.SecurityRetentionDays = auditForm.value.SecurityRetentionDays
  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildRegFieldsPatch(): UpdateRegistrationFieldsSettingsDto | undefined {
  const orig = originalRegFields.value
  if (!orig) return undefined
  const cur = regFieldsForm.value
  const patch: UpdateRegistrationFieldsSettingsDto = {}

  if (cur.Username !== orig.Username) patch.Username = cur.Username
  if (cur.Firstname !== orig.Firstname) patch.Firstname = cur.Firstname
  if (cur.Lastname !== orig.Lastname) patch.Lastname = cur.Lastname

  return Object.keys(patch).length === 0 ? undefined : patch
}

async function save() {
  const selfRegPatch = buildSelfRegPatch()
  const dcrPatch = buildDcrPatch()
  const cimdPatch = buildCimdPatch()
  const nativeGrantsPatch = buildNativeGrantsPatch()
  const browserSessionsPatch = buildBrowserSessionsPatch()
  const clientSessionsPatch = buildClientSessionsPatch()
  const authRateLimitsPatch = buildAuthRateLimitsPatch()
  const deletionPatch = buildDeletionPatch()
  const auditPatch = buildAuditPatch()
  const regFieldsPatch = buildRegFieldsPatch()
  if (!selfRegPatch && !dcrPatch && !cimdPatch && !nativeGrantsPatch && !browserSessionsPatch && !clientSessionsPatch && !authRateLimitsPatch && !deletionPatch && !auditPatch && !regFieldsPatch) {
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1200)
    return
  }
  saving.value = true
  error.value = null
  try {
    const payload: {
      SelfRegistration?: UpdateSelfRegistrationDto
      Dcr?: UpdateDcrSettingsDto
      Cimd?: UpdateCimdSettingsDto
      NativeGrants?: UpdateNativeGrantSettingsDto
      BrowserSessions?: UpdateBrowserSessionPolicyDto
      ClientSessions?: UpdateClientSessionPolicyDto
      AuthRateLimits?: UpdateAuthRateLimitsDto
      Deletion?: UpdateDeletionSettingsDto
      Audit?: UpdateAuditSettingsDto
      RegistrationFields?: UpdateRegistrationFieldsSettingsDto
    } = {}
    if (selfRegPatch) payload.SelfRegistration = selfRegPatch
    if (dcrPatch) payload.Dcr = dcrPatch
    if (cimdPatch) payload.Cimd = cimdPatch
    if (nativeGrantsPatch) payload.NativeGrants = nativeGrantsPatch
    if (browserSessionsPatch) payload.BrowserSessions = browserSessionsPatch
    if (clientSessionsPatch) payload.ClientSessions = clientSessionsPatch
    if (authRateLimitsPatch) payload.AuthRateLimits = authRateLimitsPatch
    if (deletionPatch) payload.Deletion = deletionPatch
    if (auditPatch) payload.Audit = auditPatch
    if (regFieldsPatch) payload.RegistrationFields = regFieldsPatch
    const updated = await settingsStore.patch(payload)
    originalSelfReg.value = updated.SelfRegistration
    form.value = fromDto(updated.SelfRegistration)
    originalDcr.value = updated.Dcr
    dcrForm.value = dcrFromDto(updated.Dcr)
    originalCimd.value = updated.Cimd
    cimdForm.value = cimdFromDto(updated.Cimd)
    originalNativeGrants.value = updated.NativeGrants
    nativeGrantsForm.value = nativeGrantsFromDto(updated.NativeGrants)
    originalBrowserSessions.value = updated.BrowserSessions
    browserSessionsForm.value = { ...updated.BrowserSessions }
    originalClientSessions.value = updated.ClientSessions
    clientSessionsForm.value = { ...updated.ClientSessions }
    originalAuthRateLimits.value = updated.AuthRateLimits
    authRateLimitsForm.value = authRateLimitsFromDto(updated.AuthRateLimits)
    originalDeletion.value = updated.Deletion
    deletionForm.value = deletionFromDto(updated.Deletion)
    originalAudit.value = updated.Audit
    auditForm.value = { ...updated.Audit }
    originalRegFields.value = updated.RegistrationFields
    regFieldsForm.value = regFieldsFromDto(updated.RegistrationFields)
    editingSecret.value = false
    secretInput.value = ''
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1500)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.error ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

async function rotateSigningKey() {
  try {
    rotating.value = true
    error.value = null
    const kid = await settingsStore.rotateSigningKey()
    const shortKid = kid ? kid.slice(0, 12) + '…' : ''
    toast.success(t('admin.realmSettings.signingKeys.rotated', { kid: shortKid }, 'Signing key rotated (new kid {kid}). The previous key stays valid for in-flight tokens during the overlap window.'))
  } catch (e: any) {
    const msg = e?.body?.detail ?? e?.body?.error ?? e?.message ?? String(e)
    error.value = msg
    toast.error(msg)
  } finally {
    rotating.value = false
  }
}
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4 gap-3">
    <CoarTabGroup v-model="activeTab" class="tab-bar">
      <CoarTab id="self-registration">
        {{ t('admin.realmSettings.tabs.selfRegistration', {}, 'Self-Registration') }}
      </CoarTab>
      <CoarTab id="registration-fields">
        {{ t('admin.realmSettings.tabs.registrationFields', {}, 'Pflichtfelder') }}
      </CoarTab>
      <CoarTab id="sessions">
        {{ t('admin.realmSettings.tabs.sessions', {}, 'Sessions') }}
      </CoarTab>
      <CoarTab id="dcr">
        {{ t('admin.realmSettings.tabs.dcr', {}, 'Dynamic Client Registration') }}
      </CoarTab>
      <CoarTab id="cimd">
        {{ t('admin.realmSettings.tabs.cimd', {}, 'Client ID Metadata Documents') }}
      </CoarTab>
      <CoarTab id="native-grants">
        {{ t('admin.realmSettings.tabs.nativeGrants', {}, 'Native Passwordless Grants') }}
      </CoarTab>
      <CoarTab id="auth-rate-limits">
        {{ t('admin.realmSettings.tabs.authRateLimits', {}, 'Rate Limits') }}
      </CoarTab>
      <CoarTab id="audit">
        {{ t('admin.realmSettings.tabs.audit', {}, 'Logs') }}
      </CoarTab>
      <CoarTab id="deletion">
        {{ t('admin.realmSettings.tabs.deletion', {}, 'Account Deletion') }}
      </CoarTab>
      <CoarTab v-if="canRotateSigningKey" id="signing-keys">
        {{ t('admin.realmSettings.tabs.signingKeys', {}, 'Signing Keys') }}
      </CoarTab>
      <CoarTab v-if="pageBuilderOn" id="pages">
        {{ t('admin.realmSettings.tabs.pages', {}, 'Pages') }}
      </CoarTab>
    </CoarTabGroup>

    <Notice v-if="error" variant="error">{{ error }}</Notice>
    <Notice truncate v-if="savedFlash" variant="success">
      {{ t('admin.realmSettings.saved', {}, 'Saved.') }}
    </Notice>

    <div v-if="initialLoad" class="text-sm text-gray-400">
      {{ t('common.loading', {}, 'Loading...') }}
    </div>

    <CoarCard v-else-if="activeTab === 'self-registration'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.selfReg.hint', {}, 'When enabled, visitors can create an account themselves at /register. Default: disabled.') }}
        </p>

        <CoarCheckbox
          v-model="form.Enabled"
          :label="t('admin.realmSettings.selfReg.enabled', {}, 'Enable self-registration')" />

        <template v-if="form.Enabled">
          <div class="flex flex-wrap gap-x-6 gap-y-2">
            <CoarCheckbox
              v-model="form.RequireEmailVerification"
              :label="t('admin.realmSettings.selfReg.requireEmailVerification', {}, 'Require email verification')" />
            <CoarCheckbox
              v-model="form.RequireAdminApproval"
              :label="t('admin.realmSettings.selfReg.requireAdminApproval', {}, 'Require admin approval')" />
          </div>

          <CoarFormField :label="t('admin.realmSettings.selfReg.allowedDomains', {}, 'Allowed email domains (empty = all)')">
            <EditableStringList
              v-model="form.AllowedEmailDomains"
              :placeholder="t('admin.realmSettings.selfReg.allowedDomains.placeholder', {}, 'example.com')" />
          </CoarFormField>

          <CoarFormField :label="t('admin.realmSettings.selfReg.defaultGroups', {}, 'Default groups (auto-membership after verification)')">
            <CoarMultiSelect
              v-model="form.DefaultGroupIds"
              :options="groupOptions"
              searchable
              clearable
              :placeholder="t('admin.realmSettings.selfReg.defaultGroups.placeholder', {}, 'Select groups…')" />
          </CoarFormField>

          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.selfReg.tosUrl', {}, 'Terms-of-Service URL (shows required checkbox)')">
              <CoarTextInput v-model="form.TermsOfServiceUrl" clearable placeholder="https://…" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.selfReg.privacyUrl', {}, 'Privacy Policy URL (footer link)')">
              <CoarTextInput v-model="form.PrivacyPolicyUrl" clearable placeholder="https://…" />
            </CoarFormField>
          </div>

          <CoarCheckbox
            v-model="form.CaptchaEnabled"
            :label="t('admin.realmSettings.selfReg.captchaEnabled', {}, 'Enable Cloudflare Turnstile captcha')" />

          <template v-if="form.CaptchaEnabled">
            <CoarFormField :label="t('admin.realmSettings.selfReg.captchaSiteKey', {}, 'Captcha site key (empty = Cocoar default)')">
              <CoarTextInput v-model="form.CaptchaSiteKey" clearable
                :placeholder="t('admin.realmSettings.selfReg.captchaSiteKey.placeholder', {}, 'Site key (public)')" />
            </CoarFormField>

            <CoarFormField :label="t('admin.realmSettings.selfReg.captchaSecret', {}, 'Captcha secret')">
              <div v-if="!editingSecret" class="flex items-center gap-2">
                <span class="text-sm text-gray-600">
                  <template v-if="form.CaptchaSecretSet">
                    ••••• {{ t('admin.realmSettings.selfReg.captchaSecret.set', {}, '(set — overrides default)') }}
                  </template>
                  <template v-else>
                    {{ t('admin.realmSettings.selfReg.captchaSecret.unset', {}, '(not set — uses Cocoar default)') }}
                  </template>
                </span>
                <CoarButton size="s" variant="secondary" @click="() => { editingSecret = true; secretInput = '' }">
                  {{ t('admin.realmSettings.selfReg.captchaSecret.replace', {}, 'Replace') }}
                </CoarButton>
              </div>
              <div v-else class="flex flex-col gap-1">
                <div class="flex items-center gap-2">
                  <CoarTextInput v-model="secretInput" clearable
                    :placeholder="t('admin.realmSettings.selfReg.captchaSecret.inputPlaceholder', {}, 'New secret (empty = clear/default)')" />
                  <CoarButton size="s" variant="secondary"
                    @click="() => { editingSecret = false; secretInput = '' }">
                    {{ t('common.cancel', {}, 'Cancel') }}
                  </CoarButton>
                </div>
                <p class="text-xs text-gray-500">
                  {{ t('admin.realmSettings.selfReg.captchaSecret.help', {}, 'Applied on save. Empty + save = clear secret (revert to Cocoar default).') }}
                </p>
              </div>
            </CoarFormField>
          </template>
        </template>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'registration-fields'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.regFields.hint', {}, 'Which identity fields are required at account creation (admin creation, self-registration, native passwordless registration). Email is always required. Overridable per application.') }}
        </p>

        <CoarFormField :label="t('admin.regFields.email', {}, 'E-Mail')">
          <CoarTextInput :model-value="t('admin.regFields.required', {}, 'Required')" disabled />
        </CoarFormField>
        <div class="field-enum">
          <CoarFormField :label="t('admin.regFields.username', {}, 'Benutzername')">
            <CoarSelect v-model="regFieldsForm.Username" :options="requirementOptions" />
          </CoarFormField>
        </div>
        <div class="field-enum">
          <CoarFormField :label="t('admin.regFields.firstname', {}, 'Vorname')">
            <CoarSelect v-model="regFieldsForm.Firstname" :options="requirementOptions" />
          </CoarFormField>
        </div>
        <div class="field-enum">
          <CoarFormField :label="t('admin.regFields.lastname', {}, 'Nachname')">
            <CoarSelect v-model="regFieldsForm.Lastname" :options="requirementOptions" />
          </CoarFormField>
        </div>

        <Notice v-if="regFieldsForm.Username === 'Off'" variant="info">
          {{ t('admin.regFields.usernameOffHint', {}, 'Benutzername = E-Mail (kein separates Feld).') }}
        </Notice>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'sessions'" class="p-4">
      <div class="flex flex-col gap-6">
        <section class="flex flex-col gap-3">
          <div>
            <h3 class="font-medium">
              {{ t('admin.realmSettings.sessions.browser.title', {}, 'Browser and SSO sessions') }}
            </h3>
            <p class="text-xs text-gray-500 mt-1">
              {{ t('admin.realmSettings.sessions.browser.hint', {}, 'These sessions back the signed application cookie. Idle lifetime slides while the browser is used; absolute lifetime never slides.') }}
            </p>
          </div>
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.sessions.browser.idle', {}, 'Idle lifetime (minutes)')">
              <CoarTextInput
                :model-value="String(browserSessionsForm.IdleLifetimeMinutes)"
                @update:model-value="(v) => (browserSessionsForm.IdleLifetimeMinutes = Math.max(5, parseInt(v) || 5))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.sessions.browser.absolute', {}, 'Absolute lifetime (minutes)')">
              <CoarTextInput
                :model-value="String(browserSessionsForm.AbsoluteLifetimeMinutes)"
                @update:model-value="(v) => (browserSessionsForm.AbsoluteLifetimeMinutes = Math.max(5, parseInt(v) || 5))" />
            </CoarFormField>
          </div>
          <CoarCheckbox
            v-model="browserSessionsForm.AllowRememberMe"
            :label="t('admin.realmSettings.sessions.browser.remember', {}, 'Allow persistent “remember me” cookies')" />
        </section>

        <section class="flex flex-col gap-3 border-t border-surface-200 pt-5">
          <div>
            <h3 class="font-medium">
              {{ t('admin.realmSettings.sessions.client.title', {}, 'Native app and OAuth client sessions') }}
            </h3>
            <p class="text-xs text-gray-500 mt-1">
              {{ t('admin.realmSettings.sessions.client.hint', {}, 'This is the realm default for refresh-token-backed sessions. Apps and individual OAuth clients may override it. Up to 3650 days (10 years) is supported; access tokens remain short-lived independently.') }}
            </p>
          </div>
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.sessions.client.idle', {}, 'Idle lifetime (days)')">
              <CoarTextInput
                :model-value="String(clientSessionsForm.IdleLifetimeDays)"
                @update:model-value="(v) => (clientSessionsForm.IdleLifetimeDays = Math.min(3650, Math.max(1, parseInt(v) || 1)))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.sessions.client.absolute', {}, 'Absolute lifetime (days)')">
              <CoarTextInput
                :model-value="String(clientSessionsForm.AbsoluteLifetimeDays)"
                @update:model-value="(v) => (clientSessionsForm.AbsoluteLifetimeDays = Math.min(3650, Math.max(1, parseInt(v) || 1)))" />
            </CoarFormField>
          </div>
        </section>

        <div class="flex justify-end">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'dcr'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.dcr.hint', {}, 'When enabled, AI agents and other software can register OAuth clients themselves at POST /connect/register (RFC 7591). Public PKCE clients only, no client_secret issued. Off by default.') }}
        </p>

        <CoarCheckbox
          v-model="dcrForm.Enabled"
          :label="t('admin.realmSettings.dcr.enabled', {}, 'Enable Dynamic Client Registration')" />

        <Notice truncate v-if="dcrForm.Enabled" variant="info">
          {{ t('admin.realmSettings.dcr.tripleOptInWarningShort', {}, 'Triple opt-in required before DCR clients can mint usable tokens.') }}
          <template #details>
            {{ t('admin.realmSettings.dcr.tripleOptInWarning', {}, 'Triple opt-in: clients registered here can only request access tokens for OAuth APIs with AllowDynamicRegistration enabled AND scopes with AllowDynamicRegistrationClients enabled. Until you opt in at least one API and one scope, DCR clients cannot mint usable tokens.') }}
          </template>
        </Notice>

        <template v-if="dcrForm.Enabled">
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.dcr.accessTokenMinutes', {}, 'Access token lifetime (minutes)')">
              <CoarTextInput
                :model-value="String(dcrForm.AccessTokenLifetimeMinutes)"
                @update:model-value="(v) => (dcrForm.AccessTokenLifetimeMinutes = Math.max(1, parseInt(v) || 15))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.dcr.refreshTokenDays', {}, 'Refresh token lifetime (days)')">
              <CoarTextInput
                :model-value="String(dcrForm.RefreshTokenLifetimeDays)"
                @update:model-value="(v) => (dcrForm.RefreshTokenLifetimeDays = Math.max(1, parseInt(v) || 7))" />
            </CoarFormField>
          </div>

          <div class="grid grid-cols-3 gap-3">
            <CoarFormField :label="t('admin.realmSettings.dcr.gcTtlDays', {}, 'Garbage-collect after unused (days)')">
              <CoarTextInput
                :model-value="String(dcrForm.GcTtlDays)"
                @update:model-value="(v) => (dcrForm.GcTtlDays = Math.max(1, parseInt(v) || 90))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.dcr.rateLimitIp', {}, 'Rate limit per source IP (per hour)')">
              <CoarTextInput
                :model-value="String(dcrForm.PerIpRateLimitPerHour)"
                @update:model-value="(v) => (dcrForm.PerIpRateLimitPerHour = Math.max(1, parseInt(v) || 5))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.dcr.rateLimitRealm', {}, 'Rate limit per realm (per day)')">
              <CoarTextInput
                :model-value="String(dcrForm.PerRealmRateLimitPerDay)"
                @update:model-value="(v) => (dcrForm.PerRealmRateLimitPerDay = Math.max(1, parseInt(v) || 100))" />
            </CoarFormField>
          </div>

          <CoarFormField
            :label="t('admin.realmSettings.dcr.reservedNames', {}, 'Reserved client names (substring match, NFKC + case-insensitive)')"
            :hint="t('admin.realmSettings.dcr.reservedNames.help', {}, 'Block client_name impersonation. Anything containing one of these strings is rejected at registration. Each entry is NFKC-normalised + lower-cased before comparison.')">
            <EditableStringList
              v-model="dcrForm.ReservedNames"
              :placeholder="t('admin.realmSettings.dcr.reservedNames.placeholder', {}, 'Cocoar')" />
          </CoarFormField>
        </template>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'cimd'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.cimd.hint', {}, 'When enabled, a client whose client_id is an https URL (a Client ID Metadata Document, the MCP-preferred path) is resolved on demand: the server fetches + validates the document and treats it as a public PKCE client — no registration request, no client_secret, identity bound to the URL’s domain. Off by default.') }}
        </p>

        <CoarCheckbox
          v-model="cimdForm.Enabled"
          :label="t('admin.realmSettings.cimd.enabled', {}, 'Enable Client ID Metadata Documents')" />

        <Notice truncate v-if="cimdForm.Enabled" variant="info">
          {{ t('admin.realmSettings.cimd.optInWarningShort', {}, 'Opt-in required, and the server fetches the client’s metadata URL.') }}
          <template #details>
            {{ t('admin.realmSettings.cimd.optInWarning', {}, 'Like DCR, a CIMD client can only request access tokens for OAuth APIs with AllowDynamicRegistration enabled and scopes the metadata document declares. Until you opt in at least one API, CIMD clients cannot mint usable tokens. The server fetches the client’s metadata URL — only enable this if you trust the realm’s outbound network egress.') }}
          </template>
        </Notice>

        <template v-if="cimdForm.Enabled">
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.cimd.accessTokenMinutes', {}, 'Access token lifetime (minutes)')">
              <CoarTextInput
                :model-value="String(cimdForm.AccessTokenLifetimeMinutes)"
                @update:model-value="(v) => (cimdForm.AccessTokenLifetimeMinutes = Math.max(1, parseInt(v) || 15))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.cimd.refreshTokenDays', {}, 'Refresh token lifetime (days)')">
              <CoarTextInput
                :model-value="String(cimdForm.RefreshTokenLifetimeDays)"
                @update:model-value="(v) => (cimdForm.RefreshTokenLifetimeDays = Math.max(1, parseInt(v) || 7))" />
            </CoarFormField>
          </div>
        </template>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'native-grants'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.nativeGrants.hint', {}, 'When enabled, native apps can exchange a passwordless proof directly at POST /connect/token for tokens — no browser redirect, no cookie. Three grants: urn:cocoar:otp (email one-time code), urn:cocoar:magic (magic-link token), urn:cocoar:passkey (WebAuthn assertion). Off by default.') }}
        </p>

        <CoarCheckbox
          v-model="nativeGrantsForm.Enabled"
          :label="t('admin.realmSettings.nativeGrants.enabled', {}, 'Enable native passwordless grants')" />

        <Notice truncate v-if="nativeGrantsForm.Enabled" variant="info">
          {{ t('admin.realmSettings.nativeGrants.optInWarningShort', {}, 'Per-client opt-in still required — this realm toggle alone isn’t enough.') }}
          <template #details>
            {{ t('admin.realmSettings.nativeGrants.optInWarning', {}, 'Per-client opt-in still required: a client can only use a native grant once it carries the matching grant-type permission (gt:urn:cocoar:otp / :magic / :passkey), enabled on the client’s Grants tab. Flipping this realm toggle is necessary but not sufficient. Only catalog clients qualify — DCR/CIMD clients are excluded.') }}
          </template>
        </Notice>

        <template v-if="nativeGrantsForm.Enabled">
          <div class="grid grid-cols-2 gap-3">
            <CoarFormField :label="t('admin.realmSettings.nativeGrants.accessTokenMinutes', {}, 'Access token lifetime (minutes)')">
              <CoarTextInput
                :model-value="String(nativeGrantsForm.AccessTokenLifetimeMinutes)"
                @update:model-value="(v) => (nativeGrantsForm.AccessTokenLifetimeMinutes = Math.max(1, parseInt(v) || 15))" />
            </CoarFormField>
            <CoarFormField :label="t('admin.realmSettings.nativeGrants.refreshTokenDays', {}, 'Refresh token lifetime (days)')">
              <CoarTextInput
                :model-value="String(nativeGrantsForm.RefreshTokenLifetimeDays)"
                @update:model-value="(v) => (nativeGrantsForm.RefreshTokenLifetimeDays = Math.max(1, parseInt(v) || 14))" />
            </CoarFormField>
          </div>
        </template>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'auth-rate-limits'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.authRateLimits.hint', {}, 'Per-IP request ceilings for this realm’s auth endpoints: at most PermitLimit requests per Window (minutes) from one source IP. The defaults are the secure production posture — raise them only for test realms, dev, or legitimately bursty consumers; lower them to tighten. Each value applies per realm.') }}
        </p>

        <div
          v-for="p in rateLimitPolicies"
          :key="p.key"
          class="grid grid-cols-[1fr_auto_auto] items-end gap-3">
          <div class="text-sm self-center">{{ t(p.labelKey, {}, p.fallback) }}</div>
          <CoarFormField :label="t('admin.realmSettings.authRateLimits.permitLimit', {}, 'Max requests')">
            <CoarTextInput
              class="w-28"
              :model-value="String(authRateLimitsForm[p.key].PermitLimit)"
              @update:model-value="(v) => (authRateLimitsForm[p.key].PermitLimit = Math.max(1, parseInt(v) || 1))" />
          </CoarFormField>
          <CoarFormField :label="t('admin.realmSettings.authRateLimits.windowMinutes', {}, 'Window (minutes)')">
            <CoarTextInput
              class="w-28"
              :model-value="String(authRateLimitsForm[p.key].WindowMinutes)"
              @update:model-value="(v) => (authRateLimitsForm[p.key].WindowMinutes = Math.max(1, parseInt(v) || 1))" />
          </CoarFormField>
        </div>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'audit'" class="p-4">
      <div class="flex flex-col gap-4 max-w-2xl">
        <Notice truncate variant="info">
          {{ t('admin.realmSettings.audit.hintShort', {}, 'Security events are hard-deleted after the configured retention.') }}
          <template #details>
            {{ t('admin.realmSettings.audit.hint', {}, 'Security events belong to this realm and are hard-deleted after the configured retention. The event-sourced audit history uses a separate visibility window.') }}
          </template>
        </Notice>
        <CoarFormField
          :label="t('admin.realmSettings.audit.securityRetentionDays', {}, 'Security-event retention (days)')"
          :hint="t('admin.realmSettings.audit.securityRetentionHelp', {}, 'Allowed range: 1–365 days. The security-audit-prune job deletes only expired events.')">
          <CoarTextInput
            :model-value="String(auditForm.SecurityRetentionDays)"
            @update:model-value="(v) => (auditForm.SecurityRetentionDays = Math.min(365, Math.max(1, parseInt(v) || 7)))" />
        </CoarFormField>
        <CoarFormField
          :label="t('admin.realmSettings.audit.visibilityWindowDays', {}, 'Audit-history visibility (days)')"
          :hint="t('admin.realmSettings.audit.visibilityHelp', {}, 'This hides older event-sourced audit rows; it does not delete their aggregate history.')">
          <CoarTextInput
            :model-value="String(auditForm.VisibilityWindowDays)"
            @update:model-value="(v) => (auditForm.VisibilityWindowDays = Math.max(1, parseInt(v) || 90))" />
        </CoarFormField>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'deletion'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.deletion.hint', {}, 'Controls the account-deletion lifecycle for this realm. Self-service deletions get a grace window the user can cancel during; admin deletions go to a recycle bin that is auto-purged after retention.') }}
        </p>

        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.realmSettings.deletion.graceDays', {}, 'Self-service grace period (days)')">
            <CoarTextInput
              :model-value="String(deletionForm.GraceDays)"
              @update:model-value="(v) => (deletionForm.GraceDays = Math.max(1, parseInt(v) || 30))" />
          </CoarFormField>
          <CoarFormField :label="t('admin.realmSettings.deletion.reminderLeadDays', {}, 'Reminder lead time (days before deadline)')">
            <CoarTextInput
              :model-value="String(deletionForm.ReminderLeadDays)"
              @update:model-value="(v) => (deletionForm.ReminderLeadDays = Math.max(0, parseInt(v) || 0))" />
          </CoarFormField>
        </div>

        <Notice v-if="deletionForm.ReminderLeadDays >= deletionForm.GraceDays" variant="warning">
          {{ t('admin.realmSettings.deletion.reminderTooLong', {}, 'Reminder lead time must be shorter than the grace period, otherwise the reminder never fires.') }}
        </Notice>

        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.realmSettings.deletion.adminRetentionDays', {}, 'Admin recycle-bin retention (days)')">
            <CoarTextInput
              :model-value="String(deletionForm.AdminRetentionDays)"
              @update:model-value="(v) => (deletionForm.AdminRetentionDays = Math.max(0, parseInt(v) || 30))" />
          </CoarFormField>
          <div class="flex items-end">
            <CoarCheckbox
              v-model="deletionForm.AutoPurgeEnabled"
              :label="t('admin.realmSettings.deletion.autoPurge', {}, 'Auto-purge recycle bin after retention')" />
          </div>
        </div>

        <Notice v-if="!deletionForm.AutoPurgeEnabled" variant="info">
          {{ t('admin.realmSettings.deletion.autoPurgeOff', {}, 'Auto-purge is off — admin-binned accounts are kept until an admin force-deletes them manually.') }}
        </Notice>

        <div class="flex justify-end mt-2">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'signing-keys'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.signingKeys.hint', {}, 'This realm signs its OpenIddict access and id tokens with a per-realm RSA key. Rotating generates a fresh key for new tokens; the previous key is retired but kept in the JWKS for a 30-day overlap so tokens already issued stay valid. Expired retired keys are purged automatically.') }}
        </p>

        <Notice truncate variant="warning">
          {{ t('admin.realmSettings.signingKeys.warningShort', {}, 'Rotate keys only with good reason — cached JWKS may briefly reject new tokens.') }}
          <template #details>
            {{ t('admin.realmSettings.signingKeys.warning', {}, 'Rotate only when you have reason to (suspected key exposure, scheduled hygiene). Resource servers that cache the JWKS aggressively may briefly reject new tokens until they refresh.') }}
          </template>
        </Notice>

        <div class="flex justify-end mt-2">
          <CoarPopconfirm
            :title="t('admin.realmSettings.signingKeys.rotateConfirmTitle', {}, 'Rotate signing key?')"
            :message="t('admin.realmSettings.signingKeys.rotateConfirmMessage', {}, 'A fresh key becomes active immediately. The current key is retired into the 30-day overlap window. This cannot be undone.')"
            @confirmed="rotateSigningKey">
            <CoarButton :loading="rotating" variant="danger" icon-start="rotate-ccw">
              {{ t('admin.realmSettings.signingKeys.rotateButton', {}, 'Rotate signing key') }}
            </CoarButton>
          </CoarPopconfirm>
        </div>
      </div>
    </CoarCard>

    <CoarCard v-else-if="activeTab === 'pages'" class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.realmSettings.pages.hint', {}, 'Choose which authentication page is live for this realm. "Built-in" renders the fixed default. Variants are authored in Platform → Pages.') }}
        </p>
        <Notice v-if="pagesError" variant="error">{{ pagesError }}</Notice>

        <CoarFormField v-for="m in PAGE_SLOT_META" :key="m.slug" :label="m.label">
          <CoarSelect
            :model-value="pageSlotOf(m.slug).ActiveVariantId ?? PAGE_BUILT_IN"
            :options="pageActiveOptions(pageSlotOf(m.slug))"
            :disabled="pagesBusy"
            @update:model-value="(v: string | null) => setRealmPageActive(m.slug, v)" />
        </CoarFormField>
      </div>
    </CoarCard>
  </div>
</template>

<style scoped>
.tab-bar {
  border-bottom: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  min-width: 0;
}
/* 11 wide tabs would otherwise force the tab row (and with it the whole
   settings column) past the viewport, clipping tabs and the intro text.
   Let the tab row wrap so every tab stays reachable and the column can
   shrink to the available width. */
.tab-bar :deep(.coar-tab-list) {
  flex-wrap: wrap;
}
</style>
