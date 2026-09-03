<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import {
  CoarNotice,
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarButton,
  CoarMultiSelect,
  CoarSelect,
  CoarTabGroup,
  CoarTab,
  CoarPopconfirm,
  CoarDivider,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import EditableStringList from '@/components/EditableStringList.vue'
import AuthRateLimitsEditor from '@/components/AuthRateLimitsEditor.vue'
import {
  diffRateLimitOverrides, overridesFromUpdate,
  type PolicyLimitsDto, type RateLimitEnforcementMode, type RateLimitOverrides,
} from '@/models/realmSettings'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import { useDraftStaging } from '@/composables/useDraftStaging'
import type { ManifestEntity } from '@/stores/realmDraft.store'
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
  RealmSettingsDto,
  UpdateRealmSettingsDto,
  PositionSecuritySettingsDto,
  UpdatePositionSecuritySettingsDto,
  ProofCapability,
  BindingCapability,
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

type TabId = 'registration' | 'sessions' | 'oauth-capabilities' | 'security' | 'data-retention' | 'pages'
type SavableTabId = Exclude<TabId, 'pages'>
const activeTab = ref<TabId>('registration')
const settingsContentRef = ref<HTMLElement | null>(null)

const canRotateSigningKey = computed(() => authStore.hasPermission('realm-settings:write'))
const rotating = ref(false)

// ── ADR-0005 staging: "Save area" commits the tab's patch onto the active
// draft — the manifest's Settings section IS the same UpdateRealmSettingsDto
// patch shape this view builds. Key rotation stays a live action.
const staging = useDraftStaging('settings')
const stagedSave = computed(() => staging.stagingActive.value)

/** Folds a staged/committed Settings patch into the originals + re-derives the
 * forms, so the working state equals the staged state (git: tree == index). */
function applyStagedSettings(e: ManifestEntity) {
  const s = e as UpdateRealmSettingsDto
  const foldInto = <T extends object>(orig: { value: T | null }, patch: unknown): boolean => {
    if (!orig.value || !patch || typeof patch !== 'object') return false
    const out: Record<string, unknown> = { ...(orig.value as Record<string, unknown>) }
    for (const [k, v] of Object.entries(patch as Record<string, unknown>)) {
      if (v !== undefined && k in out) out[k] = v
    }
    orig.value = out as T
    return true
  }
  if (foldInto(originalSelfReg, s.SelfRegistration)) form.value = fromDto(originalSelfReg.value!)
  // v2 merge-patch: a staged string sets the secret, a staged explicit null
  // clears it, an absent field leaves the stored secret untouched.
  const stagedCaptchaSecret = (s.SelfRegistration as Record<string, unknown> | undefined)?.CaptchaSecret
  if (stagedCaptchaSecret !== undefined && originalSelfReg.value) {
    originalSelfReg.value = {
      ...originalSelfReg.value,
      CaptchaSecretSet: typeof stagedCaptchaSecret === 'string' && stagedCaptchaSecret.length > 0,
    }
    form.value = fromDto(originalSelfReg.value)
  }
  if (foldInto(originalRegFields, s.RegistrationFields)) regFieldsForm.value = regFieldsFromDto(originalRegFields.value!)
  if (foldInto(originalBrowserSessions, s.BrowserSessions)) browserSessionsForm.value = { ...originalBrowserSessions.value! }
  if (foldInto(originalClientSessions, s.ClientSessions)) clientSessionsForm.value = { ...originalClientSessions.value! }
  if (foldInto(originalDcr, s.Dcr)) dcrForm.value = dcrFromDto(originalDcr.value!)
  if (foldInto(originalCimd, s.Cimd)) cimdForm.value = cimdFromDto(originalCimd.value!)
  if (foldInto(originalNativeGrants, s.NativeGrants)) nativeGrantsForm.value = nativeGrantsFromDto(originalNativeGrants.value!)
  if (foldInto(originalAuthRateLimits, s.AuthRateLimits)) authRateLimitsForm.value = authRateLimitsFromDto(originalAuthRateLimits.value!)
  if (foldInto(originalPositionSecurity, s.PositionSecurity)) {
    positionSecurityForm.value = {
      RequiredProofCapabilities: [...(originalPositionSecurity.value!.RequiredProofCapabilities ?? [])],
      RequiredBindingCapabilities: [...(originalPositionSecurity.value!.RequiredBindingCapabilities ?? [])],
    }
  }
  if (foldInto(originalAudit, s.Audit)) auditForm.value = { ...originalAudit.value! }
  if (foldInto(originalDeletion, s.Deletion)) deletionForm.value = deletionFromDto(originalDeletion.value!)
}

const proofCapabilityOptions = computed<Array<{ id: ProofCapability; label: string; hint: string }>>(() => [
  {
    id: 'IdentifiedActor',
    label: t('admin.realmSettings.positionSecurity.capability.identifiedActor.label', {}, 'Identified actor'),
    hint: t('admin.realmSettings.positionSecurity.capability.identifiedActor.hint', {}, 'The activation identifies an individual actor.'),
  },
  {
    id: 'PhishingResistant',
    label: t('admin.realmSettings.positionSecurity.capability.phishingResistant.label', {}, 'Phishing resistant'),
    hint: t('admin.realmSettings.positionSecurity.capability.phishingResistant.hint', {}, 'The proof resists credential forwarding and phishing.'),
  },
  {
    id: 'IndividuallyRevocable',
    label: t('admin.realmSettings.positionSecurity.capability.individuallyRevocable.label', {}, 'Individually revocable'),
    hint: t('admin.realmSettings.positionSecurity.capability.individuallyRevocable.hint', {}, 'The concrete activation credential can be revoked on its own.'),
  },
])
const bindingCapabilityOptions = computed<Array<{ id: BindingCapability; label: string; hint: string }>>(() => [
  {
    id: 'DeviceIdentity',
    label: t('admin.realmSettings.positionSecurity.capability.deviceIdentity.label', {}, 'Device identity'),
    hint: t('admin.realmSettings.positionSecurity.capability.deviceIdentity.hint', {}, 'The terminal has an individual device identity.'),
  },
  {
    id: 'SenderConstrained',
    label: t('admin.realmSettings.positionSecurity.capability.senderConstrained.label', {}, 'Sender constrained'),
    hint: t('admin.realmSettings.positionSecurity.capability.senderConstrained.hint', {}, 'Tokens can only be used by the enrolled sender key.'),
  },
])
const originalPositionSecurity = ref<PositionSecuritySettingsDto | null>(null)
const positionSecurityForm = ref<{
  RequiredProofCapabilities: ProofCapability[]
  RequiredBindingCapabilities: BindingCapability[]
}>({ RequiredProofCapabilities: [], RequiredBindingCapabilities: [] })

function setPositionCapability(
  collection: 'RequiredProofCapabilities' | 'RequiredBindingCapabilities',
  id: ProofCapability | BindingCapability,
  enabled: boolean,
) {
  if (collection === 'RequiredProofCapabilities') {
    const value = id as ProofCapability
    const values = positionSecurityForm.value.RequiredProofCapabilities
    positionSecurityForm.value.RequiredProofCapabilities = enabled
      ? Array.from(new Set([...values, value]))
      : values.filter((candidate) => candidate !== value)
  } else {
    const value = id as BindingCapability
    const values = positionSecurityForm.value.RequiredBindingCapabilities
    positionSecurityForm.value.RequiredBindingCapabilities = enabled
      ? Array.from(new Set([...values, value]))
      : values.filter((candidate) => candidate !== value)
  }
}

// ── PageBuilder: pick the active page variant per slot (ADR-0001) ──
const appConfig = useAppConfigStore()
const pageBuilderOn = computed(() => appConfig.config.Features.PageBuilder)
const pagesApi = useRealmPagesApi()
const PAGE_BUILT_IN = '__builtin__'
const PAGE_SLOT_META = [
  { slug: 'login', label: t('admin.customization.pages.login.title', {}, 'Login') },
  { slug: 'logout', label: t('admin.customization.pages.logout.title', {}, 'Logout') },
  { slug: 'password-forgot', label: t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password') },
  { slug: 'consent', label: t('admin.customization.pages.consent.title', {}, 'Consent') },
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

watch(activeTab, async (tab) => {
  if (tab === 'pages') await loadRealmPages()
  await nextTick()
  if (settingsContentRef.value) settingsContentRef.value.scrollTop = 0
})

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

// ── Auth rate limits (ADR 0007): sparse overrides per policy × dimension ──
interface AuthRateLimitsFormState {
  overrides: RateLimitOverrides
  allowlist: string[]
  /** 'auto' = no explicit mode (enforce; log-only while legacy per-IP rules exist). */
  mode: 'auto' | RateLimitEnforcementMode
  clearLegacy: boolean
}

function authRateLimitsFromDto(d: AuthRateLimitsDto): AuthRateLimitsFormState {
  return {
    overrides: overridesFromUpdate(d.Overrides),
    allowlist: [...(d.SourceAllowlist ?? [])],
    mode: d.Overrides?.Mode ?? 'auto',
    clearLegacy: false,
  }
}

const authRateLimitsForm = ref<AuthRateLimitsFormState>({ overrides: overridesFromUpdate(null), allowlist: [], mode: 'auto', clearLegacy: false })
const originalAuthRateLimits = ref<AuthRateLimitsDto | null>(null)
const rateLimitDefaults = computed<Record<string, PolicyLimitsDto>>(() => originalAuthRateLimits.value?.Defaults ?? {})
const rateLimitModeOptions = computed(() => [
  { value: 'auto', label: t('admin.rateLimits.mode.auto', {}, 'Automatic (enforce; log-only while legacy rules exist)') },
  { value: 'Enforce', label: t('admin.rateLimits.mode.enforce', {}, 'Enforce') },
  { value: 'LogOnly', label: t('admin.rateLimits.mode.logOnly', {}, 'Log only (evaluate and count, never reject)') },
])
const effectiveRateLimitMode = computed(() => {
  const m = authRateLimitsForm.value.mode
  if (m !== 'auto') return m
  const legacy = originalAuthRateLimits.value?.LegacyOverridesPresent && !authRateLimitsForm.value.clearLegacy
  return legacy ? 'LogOnly' : 'Enforce'
})

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
    originalPositionSecurity.value = dto.PositionSecurity
    positionSecurityForm.value = {
      RequiredProofCapabilities: [...(dto.PositionSecurity.RequiredProofCapabilities ?? [])],
      RequiredBindingCapabilities: [...(dto.PositionSecurity.RequiredBindingCapabilities ?? [])],
    }
    originalAuthRateLimits.value = dto.AuthRateLimits
    authRateLimitsForm.value = authRateLimitsFromDto(dto.AuthRateLimits)
    originalDeletion.value = dto.Deletion
    deletionForm.value = deletionFromDto(dto.Deletion)
    originalAudit.value = dto.Audit
    auditForm.value = { ...dto.Audit }
    originalRegFields.value = dto.RegistrationFields
    regFieldsForm.value = regFieldsFromDto(dto.RegistrationFields)
    // Staging overlay: the active draft's Settings section IS the working state.
    if (stagedSave.value && staging.draftStore.current) {
      const staged = staging.findStaged('settings')
      if (staged) applyStagedSettings(staged)
    }
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

  // v2 merge-patch lists: [] IS the clear (a null would mean "unchanged").
  if (!arrayEqual(cur.AllowedEmailDomains, orig.AllowedEmailDomains ?? []))
    patch.AllowedEmailDomains = [...cur.AllowedEmailDomains]
  if (!arrayEqual(cur.DefaultGroupIds, orig.DefaultGroupIds ?? []))
    patch.DefaultGroupIds = [...cur.DefaultGroupIds]

  const tos = cur.TermsOfServiceUrl.trim()
  if (tos !== (orig.TermsOfServiceUrl ?? '')) patch.TermsOfServiceUrl = tos || null
  const pp = cur.PrivacyPolicyUrl.trim()
  if (pp !== (orig.PrivacyPolicyUrl ?? '')) patch.PrivacyPolicyUrl = pp || null

  if (cur.CaptchaEnabled !== orig.CaptchaEnabled) patch.CaptchaEnabled = cur.CaptchaEnabled
  const key = cur.CaptchaSiteKey.trim()
  if (key !== (orig.CaptchaSiteKey ?? '')) patch.CaptchaSiteKey = key || null

  // v2 merge-patch: explicit null clears the stored secret (revert to default).
  if (editingSecret.value) patch.CaptchaSecret = secretInput.value || null

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
  const before = authRateLimitsFromDto(orig)
  const cur = authRateLimitsForm.value
  const patch: UpdateAuthRateLimitsDto = {}

  const policies = diffRateLimitOverrides(before.overrides, cur.overrides)
  if (Object.keys(policies).length) patch.Policies = policies
  if (!arrayEqual(before.allowlist, cur.allowlist)) patch.SourceAllowlist = cur.allowlist.length ? [...cur.allowlist] : null
  if (before.mode !== cur.mode) patch.Mode = cur.mode === 'auto' ? null : cur.mode
  if (cur.clearLegacy) patch.ClearLegacy = true

  return Object.keys(patch).length === 0 ? undefined : patch
}

function buildPositionSecurityPatch(): UpdatePositionSecuritySettingsDto | undefined {
  const orig = originalPositionSecurity.value
  if (!orig) return undefined
  const proof = positionSecurityForm.value.RequiredProofCapabilities
  const binding = positionSecurityForm.value.RequiredBindingCapabilities
  if (arrayEqual(proof, orig.RequiredProofCapabilities ?? []) &&
      arrayEqual(binding, orig.RequiredBindingCapabilities ?? [])) return undefined
  return {
    RequiredProofCapabilities: [...proof],
    RequiredBindingCapabilities: [...binding],
  }
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

function buildTabPayload(tab: SavableTabId): UpdateRealmSettingsDto {
  const payload: UpdateRealmSettingsDto = {}
  if (tab === 'registration') {
    payload.SelfRegistration = buildSelfRegPatch()
    payload.RegistrationFields = buildRegFieldsPatch()
  } else if (tab === 'sessions') {
    payload.BrowserSessions = buildBrowserSessionsPatch()
    payload.ClientSessions = buildClientSessionsPatch()
  } else if (tab === 'oauth-capabilities') {
    payload.Dcr = buildDcrPatch()
    payload.Cimd = buildCimdPatch()
    payload.NativeGrants = buildNativeGrantsPatch()
  } else if (tab === 'security') {
    payload.AuthRateLimits = buildAuthRateLimitsPatch()
    payload.PositionSecurity = buildPositionSecurityPatch()
  } else if (tab === 'data-retention') {
    payload.Audit = buildAuditPatch()
    payload.Deletion = buildDeletionPatch()
  }

  return Object.fromEntries(
    Object.entries(payload).filter(([, value]) => value !== undefined),
  ) as UpdateRealmSettingsDto
}

function syncSavedTab(tab: SavableTabId, updated: RealmSettingsDto) {
  // Only reset the group that was actually saved. Drafts in other tabs stay
  // untouched and retain their dirty marker instead of being silently lost.
  if (tab === 'registration') {
    originalSelfReg.value = updated.SelfRegistration
    form.value = fromDto(updated.SelfRegistration)
    originalRegFields.value = updated.RegistrationFields
    regFieldsForm.value = regFieldsFromDto(updated.RegistrationFields)
    editingSecret.value = false
    secretInput.value = ''
  } else if (tab === 'sessions') {
    originalBrowserSessions.value = updated.BrowserSessions
    browserSessionsForm.value = { ...updated.BrowserSessions }
    originalClientSessions.value = updated.ClientSessions
    clientSessionsForm.value = { ...updated.ClientSessions }
  } else if (tab === 'oauth-capabilities') {
    originalDcr.value = updated.Dcr
    dcrForm.value = dcrFromDto(updated.Dcr)
    originalCimd.value = updated.Cimd
    cimdForm.value = cimdFromDto(updated.Cimd)
    originalNativeGrants.value = updated.NativeGrants
    nativeGrantsForm.value = nativeGrantsFromDto(updated.NativeGrants)
  } else if (tab === 'security') {
    originalAuthRateLimits.value = updated.AuthRateLimits
    authRateLimitsForm.value = authRateLimitsFromDto(updated.AuthRateLimits)
    originalPositionSecurity.value = updated.PositionSecurity
    positionSecurityForm.value = {
      RequiredProofCapabilities: [...(updated.PositionSecurity.RequiredProofCapabilities ?? [])],
      RequiredBindingCapabilities: [...(updated.PositionSecurity.RequiredBindingCapabilities ?? [])],
    }
  } else if (tab === 'data-retention') {
    originalAudit.value = updated.Audit
    auditForm.value = { ...updated.Audit }
    originalDeletion.value = updated.Deletion
    deletionForm.value = deletionFromDto(updated.Deletion)
  }
}

function isTabDirty(tab: SavableTabId): boolean {
  return Object.keys(buildTabPayload(tab)).length > 0
}

const hasAnyDirty = computed(() =>
  (['registration', 'sessions', 'oauth-capabilities', 'security', 'data-retention'] as SavableTabId[])
    .some(isTabDirty),
)

const activeTabDirty = computed(() =>
  activeTab.value !== 'pages' && isTabDirty(activeTab.value),
)

const deletionInvalid = computed(() =>
  deletionForm.value.ReminderLeadDays >= deletionForm.value.GraceDays,
)

const activeTabValid = computed(() =>
  activeTab.value !== 'data-retention' || !deletionInvalid.value,
)

const canSaveActive = computed(() =>
  activeTab.value !== 'pages' && activeTabDirty.value && activeTabValid.value && !saving.value,
)

function beforeWindowUnload(event: BeforeUnloadEvent) {
  if (!hasAnyDirty.value) return
  event.preventDefault()
}

onMounted(() => window.addEventListener('beforeunload', beforeWindowUnload))
onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeWindowUnload))

onBeforeRouteLeave(() => {
  if (!hasAnyDirty.value) return true
  return confirm(t(
    'admin.realmSettings.unsavedLeave',
    {},
    'There are unsaved realm settings. Leave the page and discard them?',
  ))
})

async function save(tab: SavableTabId) {
  const payload = buildTabPayload(tab)
  if (Object.keys(payload).length === 0) return

  saving.value = true
  error.value = null
  try {
    // ADR-0005: commit onto the active draft instead of writing live. Section
    // patches merge over the already-staged Settings entity; position-security
    // consequences happen at APPLY (no live preview here).
    if (stagedSave.value) {
      const base = staging.findStaged('settings') ?? {}
      const merged: ManifestEntity = { ...base }
      for (const [section, patch] of Object.entries(payload)) {
        if (patch === undefined) continue
        const prev = (merged[section] ?? {}) as Record<string, unknown>
        merged[section] = (typeof patch === 'object' && patch !== null && !Array.isArray(patch))
          ? { ...prev, ...patch }
          : patch
      }
      await staging.stage('settings', merged)
      applyStagedSettings(payload as ManifestEntity)
      editingSecret.value = false
      secretInput.value = ''
      savedFlash.value = true
      setTimeout(() => { savedFlash.value = false }, 1500)
      return
    }
    if (payload.PositionSecurity) {
      const consequences = await settingsStore.previewPositionSecurity(payload.PositionSecurity)
      if (consequences.HasConsequences) {
        const confirmed = confirm(t(
          'admin.realmSettings.positionSecurity.confirm',
          {
            positions: consequences.Positions.length,
            terminals: consequences.TerminalIds.length,
            sessions: consequences.StaffingSessionIds.length,
          },
          `This floor makes ${consequences.Positions.length} positions and ${consequences.TerminalIds.length} terminal slots non-conforming, and immediately ends ${consequences.StaffingSessionIds.length} active staffing sessions. Continue?`,
        ))
        if (!confirmed) return
        payload.ConfirmPositionSecurityConsequences = true
      }
    }
    const updated = await settingsStore.patch(payload)
    syncSavedTab(tab, updated)
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1500)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.error ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

function saveActiveTab() {
  if (activeTab.value === 'pages') return
  return save(activeTab.value)
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
  <div class="realm-settings-page">
    <div class="realm-settings-shell">
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="registration">
          <span class="tab-label">
            {{ t('admin.realmSettings.tabs.registration', {}, 'Registration') }}
            <span v-if="isTabDirty('registration')" class="dirty-dot" aria-hidden="true" />
          </span>
        </CoarTab>
        <CoarTab id="sessions">
          <span class="tab-label">
            {{ t('admin.realmSettings.tabs.sessions', {}, 'Sessions') }}
            <span v-if="isTabDirty('sessions')" class="dirty-dot" aria-hidden="true" />
          </span>
        </CoarTab>
        <CoarTab id="oauth-capabilities">
          <span class="tab-label">
            {{ t('admin.realmSettings.tabs.oauthCapabilities', {}, 'OAuth & Clients') }}
            <span v-if="isTabDirty('oauth-capabilities')" class="dirty-dot" aria-hidden="true" />
          </span>
        </CoarTab>
        <CoarTab id="security">
          <span class="tab-label">
            {{ t('admin.realmSettings.tabs.security', {}, 'Security') }}
            <span v-if="isTabDirty('security')" class="dirty-dot" aria-hidden="true" />
          </span>
        </CoarTab>
        <CoarTab id="data-retention">
          <span class="tab-label">
            {{ t('admin.realmSettings.tabs.dataRetention', {}, 'Data & Retention') }}
            <span v-if="isTabDirty('data-retention')" class="dirty-dot" aria-hidden="true" />
          </span>
        </CoarTab>
        <CoarTab v-if="pageBuilderOn" id="pages">
          {{ t('admin.realmSettings.tabs.pages', {}, 'Sign-in pages') }}
        </CoarTab>
      </CoarTabGroup>

      <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>
      <CoarNotice truncate v-if="savedFlash" variant="success">
        {{ t('admin.realmSettings.saved', {}, 'Saved.') }}
      </CoarNotice>

      <div v-if="initialLoad" class="text-sm text-gray-400">
        {{ t('common.loading', {}, 'Loading...') }}
      </div>

      <div v-else ref="settingsContentRef" class="settings-content">
        <!-- Registration: self-service posture + identity field policy. -->
        <template v-if="activeTab === 'registration'">
          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.selfRegistration', {}, 'Self-Registration') }}</h2>
            </CoarDivider>
            <div class="section-intro">
              <p>{{ t('admin.realmSettings.selfReg.hint', {}, 'When enabled, visitors can create an account themselves at /register. Default: disabled.') }}</p>
              <CoarCheckbox
                v-model="form.Enabled"
                :label="t('common.active', {}, 'Active')" />
            </div>

            <div v-if="form.Enabled" class="section-body">
              <div class="inline-checks">
                <CoarCheckbox
                  v-model="form.RequireEmailVerification"
                  :label="t('admin.realmSettings.selfReg.requireEmailVerification', {}, 'Require email verification')" />
                <CoarCheckbox
                  v-model="form.RequireAdminApproval"
                  :label="t('admin.realmSettings.selfReg.requireAdminApproval', {}, 'Require admin approval')" />
              </div>

              <EditableStringList
                v-model="form.AllowedEmailDomains"
                appearance="compact-grid"
                min-height="11rem"
                :header-label="t('admin.realmSettings.selfReg.allowedDomains', {}, 'Allowed email domains (empty = all)')"
                :header-hint="t('admin.realmSettings.selfReg.allowedDomainsHint', {}, 'Leave empty to allow every e-mail domain.')"
                :add-label="t('admin.realmSettings.selfReg.addDomain', {}, 'Add domain')"
                :placeholder="t('admin.realmSettings.selfReg.allowedDomains.placeholder', {}, 'example.com')" />

              <CoarFormField :label="t('admin.realmSettings.selfReg.defaultGroups', {}, 'Default groups (auto-membership after verification)')">
                <CoarMultiSelect
                  v-model="form.DefaultGroupIds"
                  :options="groupOptions"
                  searchable
                  clearable
                  :placeholder="t('admin.realmSettings.selfReg.defaultGroups.placeholder', {}, 'Select groups…')" />
              </CoarFormField>

              <div class="form-grid-2">
                <CoarFormField :label="t('admin.realmSettings.selfReg.tosUrl', {}, 'Terms-of-Service URL')">
                  <CoarTextInput v-model="form.TermsOfServiceUrl" clearable placeholder="https://…" />
                </CoarFormField>
                <CoarFormField :label="t('admin.realmSettings.selfReg.privacyUrl', {}, 'Privacy Policy URL')">
                  <CoarTextInput v-model="form.PrivacyPolicyUrl" clearable placeholder="https://…" />
                </CoarFormField>
              </div>

              <CoarCheckbox
                v-model="form.CaptchaEnabled"
                :label="t('admin.realmSettings.selfReg.captchaEnabled', {}, 'Enable Cloudflare Turnstile captcha')" />

              <div v-if="form.CaptchaEnabled" class="form-grid-2">
                <CoarFormField :label="t('admin.realmSettings.selfReg.captchaSiteKey', {}, 'Captcha site key')">
                  <CoarTextInput v-model="form.CaptchaSiteKey" clearable
                    :placeholder="t('admin.realmSettings.selfReg.captchaSiteKey.placeholder', {}, 'Site key (public)')" />
                </CoarFormField>
                <CoarFormField :label="t('admin.realmSettings.selfReg.captchaSecret', {}, 'Captcha secret')">
                  <div v-if="!editingSecret" class="secret-row">
                    <span class="field-value-muted">
                      {{ form.CaptchaSecretSet
                        ? t('admin.realmSettings.selfReg.captchaSecret.set', {}, 'Set — overrides default')
                        : t('admin.realmSettings.selfReg.captchaSecret.unset', {}, 'Not set — uses default') }}
                    </span>
                    <CoarButton size="s" variant="secondary" @click="() => { editingSecret = true; secretInput = '' }">
                      {{ t('admin.realmSettings.selfReg.captchaSecret.replace', {}, 'Replace') }}
                    </CoarButton>
                  </div>
                  <div v-else class="secret-row">
                    <CoarTextInput v-model="secretInput" clearable
                      :placeholder="t('admin.realmSettings.selfReg.captchaSecret.inputPlaceholder', {}, 'New secret (empty = clear/default)')" />
                    <CoarButton size="s" variant="secondary" @click="() => { editingSecret = false; secretInput = '' }">
                      {{ t('common.cancel', {}, 'Cancel') }}
                    </CoarButton>
                  </div>
                </CoarFormField>
              </div>
            </div>
          </section>

          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.registrationFields', {}, 'Registration fields') }}</h2>
            </CoarDivider>
            <p class="section-description">
              {{ t('admin.realmSettings.regFields.hint', {}, 'Which identity fields are required at account creation. E-mail is always required. Applications may override this policy.') }}
            </p>
            <div class="policy-grid">
              <div class="policy-grid__header">
                <span>{{ t('admin.realmSettings.field', {}, 'Field') }}</span>
                <span>{{ t('admin.realmSettings.requirement', {}, 'Requirement') }}</span>
              </div>
              <div class="policy-grid__row">
                <span>{{ t('admin.regFields.email', {}, 'E-Mail') }}</span>
                <span class="fixed-policy">{{ t('admin.regFields.required', {}, 'Required') }}</span>
              </div>
              <div class="policy-grid__row">
                <span>{{ t('admin.regFields.username', {}, 'Username') }}</span>
                <CoarSelect v-model="regFieldsForm.Username" :options="requirementOptions" />
              </div>
              <div class="policy-grid__row">
                <span>{{ t('admin.regFields.firstname', {}, 'First name') }}</span>
                <CoarSelect v-model="regFieldsForm.Firstname" :options="requirementOptions" />
              </div>
              <div class="policy-grid__row">
                <span>{{ t('admin.regFields.lastname', {}, 'Last name') }}</span>
                <CoarSelect v-model="regFieldsForm.Lastname" :options="requirementOptions" />
              </div>
            </div>
            <CoarNotice v-if="regFieldsForm.Username === 'Off'" variant="info">
              {{ t('admin.regFields.usernameOffHint', {}, 'Username = e-mail (no separate field).') }}
            </CoarNotice>
          </section>
        </template>

        <!-- Session defaults. -->
        <template v-else-if="activeTab === 'sessions'">
          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sessions.browser.title', {}, 'Browser and SSO sessions') }}</h2>
            </CoarDivider>
            <p class="section-description">{{ t('admin.realmSettings.sessions.browser.hint', {}, 'These sessions back the signed application cookie. Idle lifetime slides while the browser is used; absolute lifetime never slides.') }}</p>
            <div class="form-grid-2">
              <CoarFormField :label="t('admin.realmSettings.sessions.browser.idle', {}, 'Idle lifetime (minutes)')">
                <CoarTextInput :model-value="String(browserSessionsForm.IdleLifetimeMinutes)"
                  @update:model-value="(v) => (browserSessionsForm.IdleLifetimeMinutes = Math.max(5, parseInt(v) || 5))" />
              </CoarFormField>
              <CoarFormField :label="t('admin.realmSettings.sessions.browser.absolute', {}, 'Absolute lifetime (minutes)')">
                <CoarTextInput :model-value="String(browserSessionsForm.AbsoluteLifetimeMinutes)"
                  @update:model-value="(v) => (browserSessionsForm.AbsoluteLifetimeMinutes = Math.max(5, parseInt(v) || 5))" />
              </CoarFormField>
            </div>
            <CoarCheckbox v-model="browserSessionsForm.AllowRememberMe"
              :label="t('admin.realmSettings.sessions.browser.remember', {}, 'Allow persistent remember-me cookies')" />
          </section>

          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sessions.client.title', {}, 'Native app and OAuth client sessions') }}</h2>
            </CoarDivider>
            <p class="section-description">{{ t('admin.realmSettings.sessions.client.hint', {}, 'Realm default for refresh-token-backed sessions. Applications and individual OAuth clients may override it.') }}</p>
            <div class="form-grid-2">
              <CoarFormField :label="t('admin.realmSettings.sessions.client.idle', {}, 'Idle lifetime (days)')">
                <CoarTextInput :model-value="String(clientSessionsForm.IdleLifetimeDays)"
                  @update:model-value="(v) => (clientSessionsForm.IdleLifetimeDays = Math.min(3650, Math.max(1, parseInt(v) || 1)))" />
              </CoarFormField>
              <CoarFormField :label="t('admin.realmSettings.sessions.client.absolute', {}, 'Absolute lifetime (days)')">
                <CoarTextInput :model-value="String(clientSessionsForm.AbsoluteLifetimeDays)"
                  @update:model-value="(v) => (clientSessionsForm.AbsoluteLifetimeDays = Math.min(3650, Math.max(1, parseInt(v) || 1)))" />
              </CoarFormField>
            </div>
          </section>
        </template>

        <!-- Advanced OAuth client onboarding and native grants. -->
        <template v-else-if="activeTab === 'oauth-capabilities'">
          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.dcr', {}, 'Dynamic Client Registration') }}</h2>
            </CoarDivider>
            <div class="section-intro">
              <p>{{ t('admin.realmSettings.dcr.hint', {}, 'Allow software to register public PKCE clients through RFC 7591.') }}</p>
              <CoarCheckbox v-model="dcrForm.Enabled" :label="t('common.active', {}, 'Active')" />
            </div>
            <CoarNotice truncate v-if="dcrForm.Enabled" variant="info">
              {{ t('admin.realmSettings.dcr.tripleOptInWarningShort', {}, 'Triple opt-in required before DCR clients can mint usable tokens.') }}
              <template #details>{{ t('admin.realmSettings.dcr.tripleOptInWarning', {}, 'APIs and scopes must explicitly allow dynamically registered clients.') }}</template>
            </CoarNotice>
            <div v-if="dcrForm.Enabled" class="section-body">
              <div class="form-grid-2">
                <CoarFormField :label="t('admin.realmSettings.dcr.accessTokenMinutes', {}, 'Access-token lifetime (minutes)')">
                  <CoarTextInput :model-value="String(dcrForm.AccessTokenLifetimeMinutes)" @update:model-value="(v) => (dcrForm.AccessTokenLifetimeMinutes = Math.max(1, parseInt(v) || 15))" />
                </CoarFormField>
                <CoarFormField :label="t('admin.realmSettings.dcr.refreshTokenDays', {}, 'Refresh-token lifetime (days)')">
                  <CoarTextInput :model-value="String(dcrForm.RefreshTokenLifetimeDays)" @update:model-value="(v) => (dcrForm.RefreshTokenLifetimeDays = Math.max(1, parseInt(v) || 7))" />
                </CoarFormField>
              </div>
              <div class="form-grid-3">
                <CoarFormField :label="t('admin.realmSettings.dcr.gcTtlDays', {}, 'Remove after inactivity (days)')">
                  <CoarTextInput :model-value="String(dcrForm.GcTtlDays)" @update:model-value="(v) => (dcrForm.GcTtlDays = Math.max(1, parseInt(v) || 90))" />
                </CoarFormField>
                <CoarFormField :label="t('admin.realmSettings.dcr.rateLimitIp', {}, 'Per source IP / hour')">
                  <CoarTextInput :model-value="String(dcrForm.PerIpRateLimitPerHour)" @update:model-value="(v) => (dcrForm.PerIpRateLimitPerHour = Math.max(1, parseInt(v) || 5))" />
                </CoarFormField>
                <CoarFormField :label="t('admin.realmSettings.dcr.rateLimitRealm', {}, 'Per realm / day')">
                  <CoarTextInput :model-value="String(dcrForm.PerRealmRateLimitPerDay)" @update:model-value="(v) => (dcrForm.PerRealmRateLimitPerDay = Math.max(1, parseInt(v) || 100))" />
                </CoarFormField>
              </div>
              <EditableStringList
                v-model="dcrForm.ReservedNames"
                appearance="compact-grid"
                min-height="11rem"
                :header-label="t('admin.realmSettings.dcr.reservedNames', {}, 'Reserved client names')"
                :header-hint="t('admin.realmSettings.dcr.reservedNames.help', {}, 'Blocks impersonating client names using normalized substring matching.')"
                :add-label="t('admin.realmSettings.dcr.addReservedName', {}, 'Add name')"
                :placeholder="t('admin.realmSettings.dcr.reservedNames.placeholder', {}, 'Cocoar')" />
            </div>
          </section>

          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.cimd', {}, 'Client-ID Metadata Documents (CIMD)') }}</h2>
            </CoarDivider>
            <div class="section-intro">
              <p>{{ t('admin.realmSettings.cimd.hint', {}, 'Resolve HTTPS client IDs as metadata documents and treat them as public PKCE clients.') }}</p>
              <CoarCheckbox v-model="cimdForm.Enabled" :label="t('common.active', {}, 'Active')" />
            </div>
            <CoarNotice truncate v-if="cimdForm.Enabled" variant="info">
              {{ t('admin.realmSettings.cimd.optInWarningShort', {}, 'Opt-in required, and the server fetches the client metadata URL.') }}
              <template #details>{{ t('admin.realmSettings.cimd.optInWarning', {}, 'Enable only when outbound network access is trusted.') }}</template>
            </CoarNotice>
            <div v-if="cimdForm.Enabled" class="form-grid-2 section-body">
              <CoarFormField :label="t('admin.realmSettings.cimd.accessTokenMinutes', {}, 'Access-token lifetime (minutes)')">
                <CoarTextInput :model-value="String(cimdForm.AccessTokenLifetimeMinutes)" @update:model-value="(v) => (cimdForm.AccessTokenLifetimeMinutes = Math.max(1, parseInt(v) || 15))" />
              </CoarFormField>
              <CoarFormField :label="t('admin.realmSettings.cimd.refreshTokenDays', {}, 'Refresh-token lifetime (days)')">
                <CoarTextInput :model-value="String(cimdForm.RefreshTokenLifetimeDays)" @update:model-value="(v) => (cimdForm.RefreshTokenLifetimeDays = Math.max(1, parseInt(v) || 7))" />
              </CoarFormField>
            </div>
          </section>

          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.nativeGrants', {}, 'Native passwordless grants') }}</h2>
            </CoarDivider>
            <div class="section-intro">
              <p>{{ t('admin.realmSettings.nativeGrants.hint', {}, 'Allow native apps to exchange passwordless proofs without a browser redirect.') }}</p>
              <CoarCheckbox v-model="nativeGrantsForm.Enabled" :label="t('common.active', {}, 'Active')" />
            </div>
            <CoarNotice truncate v-if="nativeGrantsForm.Enabled" variant="info">
              {{ t('admin.realmSettings.nativeGrants.optInWarningShort', {}, 'Per-client opt-in is still required.') }}
              <template #details>{{ t('admin.realmSettings.nativeGrants.optInWarning', {}, 'Each OAuth client must explicitly allow the corresponding grant type.') }}</template>
            </CoarNotice>
            <div v-if="nativeGrantsForm.Enabled" class="form-grid-2 section-body">
              <CoarFormField :label="t('admin.realmSettings.nativeGrants.accessTokenMinutes', {}, 'Access-token lifetime (minutes)')">
                <CoarTextInput :model-value="String(nativeGrantsForm.AccessTokenLifetimeMinutes)" @update:model-value="(v) => (nativeGrantsForm.AccessTokenLifetimeMinutes = Math.max(1, parseInt(v) || 15))" />
              </CoarFormField>
              <CoarFormField :label="t('admin.realmSettings.nativeGrants.refreshTokenDays', {}, 'Refresh-token lifetime (days)')">
                <CoarTextInput :model-value="String(nativeGrantsForm.RefreshTokenLifetimeDays)" @update:model-value="(v) => (nativeGrantsForm.RefreshTokenLifetimeDays = Math.max(1, parseInt(v) || 14))" />
              </CoarFormField>
            </div>
          </section>
        </template>

        <!-- Security posture and key material. -->
        <template v-else-if="activeTab === 'security'">
          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">
                {{ t('admin.realmSettings.positionSecurity.title', {}, 'Position-terminal security floor') }}
              </h2>
            </CoarDivider>
            <p class="section-description">
              {{ t('admin.realmSettings.positionSecurity.hint', {}, 'Every activation method and terminal binding allowed by a position must provide all selected capabilities. Tightening is previewed before it takes effect.') }}
            </p>
            <div class="form-grid-2">
              <div class="rounded border border-surface-200 p-3">
                <h3 class="mb-3 text-sm font-semibold">
                  {{ t('admin.realmSettings.positionSecurity.proofCapabilities', {}, 'Required proof capabilities') }}
                </h3>
                <div v-for="option in proofCapabilityOptions" :key="option.id" class="mb-3 flex items-start gap-2">
                  <CoarCheckbox
                    :model-value="positionSecurityForm.RequiredProofCapabilities.includes(option.id)"
                    @update:model-value="(value) => setPositionCapability('RequiredProofCapabilities', option.id, !!value)" />
                  <div>
                    <div class="text-sm">{{ option.label }}</div>
                    <div class="text-xs text-surface-500">{{ option.hint }}</div>
                  </div>
                </div>
              </div>
              <div class="rounded border border-surface-200 p-3">
                <h3 class="mb-3 text-sm font-semibold">
                  {{ t('admin.realmSettings.positionSecurity.bindingCapabilities', {}, 'Required binding capabilities') }}
                </h3>
                <div v-for="option in bindingCapabilityOptions" :key="option.id" class="mb-3 flex items-start gap-2">
                  <CoarCheckbox
                    :model-value="positionSecurityForm.RequiredBindingCapabilities.includes(option.id)"
                    @update:model-value="(value) => setPositionCapability('RequiredBindingCapabilities', option.id, !!value)" />
                  <div>
                    <div class="text-sm">{{ option.label }}</div>
                    <div class="text-xs text-surface-500">{{ option.hint }}</div>
                  </div>
                </div>
              </div>
            </div>
            <CoarNotice variant="warning">
              {{ t('admin.realmSettings.positionSecurity.warning', {}, 'A confirmed tightening ends affected staffing sessions immediately. Non-conforming slots cannot activate again until their position policy is corrected.') }}
            </CoarNotice>
          </section>

          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.rateLimits', {}, 'Authentication rate limits') }}</h2>
            </CoarDivider>
            <p class="section-description">{{ t('admin.rateLimits.hint', {}, 'Multi-dimensional ceilings for the public auth endpoints. Target and App are the defence (mailbox, mail budget), Client bounds one integration, Source is only a coarse brake sized for shared addresses (NAT). Empty cells inherit the shipped defaults.') }}</p>

            <CoarNotice v-if="originalAuthRateLimits?.LegacyOverridesPresent" variant="warning" class="mb-3">
              {{ t('admin.rateLimits.legacyNotice', {}, 'This realm still carries per-IP rules from before the multi-dimensional limits. They are not applied any more; until a mode is chosen the realm runs log-only.') }}
              <CoarCheckbox v-model="authRateLimitsForm.clearLegacy" :label="t('admin.rateLimits.clearLegacy', {}, 'Remove the legacy per-IP rules on save')" />
            </CoarNotice>

            <div class="grid grid-cols-2 gap-3 mb-3">
              <CoarFormField
                :label="t('admin.rateLimits.mode.label', {}, 'Enforcement')"
                :hint="t('admin.rateLimits.mode.hint', {}, 'Log-only evaluates and counts every dimension without rejecting — the rollout mode for sizing Source against real traffic.')">
                <CoarSelect v-model="authRateLimitsForm.mode" :options="rateLimitModeOptions" />
              </CoarFormField>
              <p class="section-description self-end">
                {{ t('admin.rateLimits.effectiveMode', {}, 'Effective') }}: <strong>{{ effectiveRateLimitMode }}</strong>
              </p>
            </div>

            <EditableStringList
              v-model="authRateLimitsForm.allowlist"
              appearance="compact-grid"
              min-height="8rem"
              class="mb-3"
              :header-label="t('admin.rateLimits.allowlist', {}, 'Source allowlist — addresses or CIDR ranges exempt from the Source ceilings only (Target, Client and App still apply)')" />

            <AuthRateLimitsEditor v-model="authRateLimitsForm.overrides" :baseline="rateLimitDefaults" />
          </section>

          <section v-if="canRotateSigningKey" class="settings-section danger-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.signingKeys', {}, 'Signing keys') }}</h2>
            </CoarDivider>
            <p class="section-description">{{ t('admin.realmSettings.signingKeys.hint', {}, 'This realm signs access and ID tokens with its own RSA key.') }}</p>
            <CoarNotice truncate variant="warning">
              {{ t('admin.realmSettings.signingKeys.warningShort', {}, 'Rotate keys only with good reason — cached JWKS may briefly reject new tokens.') }}
              <template #details>{{ t('admin.realmSettings.signingKeys.warning', {}, 'Resource servers may briefly reject tokens while refreshing cached JWKS.') }}</template>
            </CoarNotice>
            <div class="danger-action">
              <CoarPopconfirm
                :title="t('admin.realmSettings.signingKeys.rotateConfirmTitle', {}, 'Rotate signing key?')"
                :message="t('admin.realmSettings.signingKeys.rotateConfirmMessage', {}, 'A fresh key becomes active immediately. This cannot be undone.')"
                @confirmed="rotateSigningKey">
                <CoarButton :loading="rotating" variant="danger" icon-start="rotate-ccw">
                  {{ t('admin.realmSettings.signingKeys.rotateButton', {}, 'Rotate signing key') }}
                </CoarButton>
              </CoarPopconfirm>
            </div>
          </section>
        </template>

        <!-- Realm-owned data visibility, retention and account deletion. -->
        <template v-else-if="activeTab === 'data-retention'">
          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.auditRetention', {}, 'Protocol retention') }}</h2>
            </CoarDivider>
            <CoarNotice truncate variant="info">
              {{ t('admin.realmSettings.audit.hintShort', {}, 'Security events are hard-deleted after the configured retention.') }}
              <template #details>{{ t('admin.realmSettings.audit.hint', {}, 'Security-event retention deletes rows; audit visibility only hides older history.') }}</template>
            </CoarNotice>
            <div class="form-grid-2">
              <CoarFormField
                :label="t('admin.realmSettings.audit.securityRetentionDays', {}, 'Security-event retention (days)')"
                :hint="t('admin.realmSettings.audit.securityRetentionHelp', {}, 'Allowed range: 1–365 days.')">
                <CoarTextInput :model-value="String(auditForm.SecurityRetentionDays)"
                  @update:model-value="(v) => (auditForm.SecurityRetentionDays = Math.min(365, Math.max(1, parseInt(v) || 7)))" />
              </CoarFormField>
              <CoarFormField
                :label="t('admin.realmSettings.audit.visibilityWindowDays', {}, 'Audit-history visibility (days)')"
                :hint="t('admin.realmSettings.audit.visibilityHelp', {}, 'Hides older event-sourced audit rows without deleting aggregate history.')">
                <CoarTextInput :model-value="String(auditForm.VisibilityWindowDays)"
                  @update:model-value="(v) => (auditForm.VisibilityWindowDays = Math.max(1, parseInt(v) || 90))" />
              </CoarFormField>
            </div>
          </section>

          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.accountDeletion', {}, 'Account deletion') }}</h2>
            </CoarDivider>
            <p class="section-description">{{ t('admin.realmSettings.deletion.hint', {}, 'Controls self-service grace periods and the administrator recycle bin.') }}</p>
            <div class="form-grid-2">
              <CoarFormField :label="t('admin.realmSettings.deletion.graceDays', {}, 'Self-service grace period (days)')">
                <CoarTextInput :model-value="String(deletionForm.GraceDays)" @update:model-value="(v) => (deletionForm.GraceDays = Math.max(1, parseInt(v) || 30))" />
              </CoarFormField>
              <CoarFormField :label="t('admin.realmSettings.deletion.reminderLeadDays', {}, 'Reminder lead time (days)')"
                :error="deletionInvalid ? t('admin.realmSettings.deletion.reminderTooLong', {}, 'Reminder lead time must be shorter than the grace period.') : ''">
                <CoarTextInput :model-value="String(deletionForm.ReminderLeadDays)" @update:model-value="(v) => (deletionForm.ReminderLeadDays = Math.max(0, parseInt(v) || 0))" />
              </CoarFormField>
              <CoarFormField :label="t('admin.realmSettings.deletion.adminRetentionDays', {}, 'Administrator recycle-bin retention (days)')">
                <CoarTextInput :model-value="String(deletionForm.AdminRetentionDays)" @update:model-value="(v) => (deletionForm.AdminRetentionDays = Math.max(0, parseInt(v) || 30))" />
              </CoarFormField>
              <div class="checkbox-field">
                <CoarCheckbox v-model="deletionForm.AutoPurgeEnabled"
                  :label="t('admin.realmSettings.deletion.autoPurge', {}, 'Auto-purge recycle bin after retention')" />
              </div>
            </div>
            <CoarNotice v-if="!deletionForm.AutoPurgeEnabled" variant="info">
              {{ t('admin.realmSettings.deletion.autoPurgeOff', {}, 'Auto-purge is off — accounts remain until an administrator force-deletes them.') }}
            </CoarNotice>
          </section>
        </template>

        <!-- Page selections save immediately; variants are authored elsewhere. -->
        <template v-else-if="activeTab === 'pages'">
          <section class="settings-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h2 class="section-title">{{ t('admin.realmSettings.sections.pages', {}, 'Active sign-in pages') }}</h2>
            </CoarDivider>
            <p class="section-description">{{ t('admin.realmSettings.pages.hint', {}, 'Choose which authentication page is live for this realm. Changes are applied immediately.') }}</p>
            <CoarNotice v-if="pagesError" variant="error">{{ pagesError }}</CoarNotice>
            <div class="form-grid-2">
              <CoarFormField v-for="m in PAGE_SLOT_META" :key="m.slug" :label="m.label">
                <CoarSelect
                  :model-value="pageSlotOf(m.slug).ActiveVariantId ?? PAGE_BUILT_IN"
                  :options="pageActiveOptions(pageSlotOf(m.slug))"
                  :disabled="pagesBusy"
                  @update:model-value="(v: string | null) => setRealmPageActive(m.slug, v)" />
              </CoarFormField>
            </div>
          </section>
        </template>

        <div v-if="activeTab !== 'pages'" class="save-bar">
          <span :class="activeTabDirty ? 'save-status save-status--dirty' : 'save-status'">
            {{ activeTabDirty
              ? t('admin.realmSettings.unsaved', {}, 'Unsaved changes in this area.')
              : t('admin.realmSettings.noUnsaved', {}, 'No unsaved changes.') }}
          </span>
          <CoarButton :loading="saving" :disabled="!canSaveActive" @click="saveActiveTab">
            {{ stagedSave
              ? t('admin.realmConfig.entry.save', {}, 'In den Draft übernehmen')
              : t('admin.realmSettings.saveArea', {}, 'Save area') }}
          </CoarButton>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.realm-settings-page {
  display: flex;
  flex: 1;
  min-height: 0;
  min-width: 0;
  overflow: hidden;
  padding: 1rem;
}

.realm-settings-shell {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
  max-width: 72rem;
  overflow: hidden;
  gap: 0.75rem;
}

.tab-bar {
  border-bottom: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  min-width: 0;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
}

.dirty-dot {
  width: 0.45rem;
  height: 0.45rem;
  border-radius: 50%;
  background: var(--coar-background-semantic-warning, #d97706);
}

.settings-content {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  gap: 1.25rem;
  overflow-x: hidden;
  overflow-y: auto;
  padding-right: 0.25rem;
  scrollbar-gutter: stable;
}

.settings-section {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.section-title {
  margin: 0;
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.75rem;
  font-weight: 650;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.section-description,
.section-intro p {
  margin: 0;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.8rem;
  line-height: 1.45;
}

.section-intro {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: start;
  gap: 1.5rem;
}

.section-body {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.inline-checks,
.secret-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.75rem 1.5rem;
}

.secret-row > :first-child {
  flex: 1;
}

.field-value-muted {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.85rem;
}

.form-grid-2,
.form-grid-3 {
  display: grid;
  gap: 0.85rem;
}

.form-grid-2 { grid-template-columns: repeat(2, minmax(0, 1fr)); }
.form-grid-3 { grid-template-columns: repeat(3, minmax(0, 1fr)); }

.policy-grid,
.rate-limit-grid {
  overflow: hidden;
  border: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  border-radius: var(--coar-radius-m, 4px);
}

.policy-grid__header,
.policy-grid__row {
  display: grid;
  grid-template-columns: minmax(12rem, 1fr) minmax(12rem, 20rem);
  align-items: center;
  gap: 1rem;
  min-height: 3rem;
  padding: 0.45rem 0.75rem;
}

.rate-limit-grid__header,
.rate-limit-grid__row {
  display: grid;
  grid-template-columns: minmax(18rem, 1fr) 9rem 10rem;
  align-items: center;
  gap: 0.75rem;
  min-height: 3rem;
  padding: 0.4rem 0.75rem;
}

.policy-grid__header,
.rate-limit-grid__header {
  color: var(--coar-text-neutral-secondary, #525e76);
  background: var(--coar-background-neutral-secondary, #f8fafc);
  font-size: 0.75rem;
  font-weight: 650;
}

.policy-grid__row + .policy-grid__row,
.rate-limit-grid__row + .rate-limit-grid__row {
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
}

.policy-grid__row,
.rate-limit-grid__row {
  font-size: 0.85rem;
}

.fixed-policy {
  color: var(--coar-text-neutral-secondary, #525e76);
  font-weight: 600;
}

.compact-number { width: 100%; }

.checkbox-field {
  display: flex;
  align-items: end;
  padding-bottom: 0.6rem;
}

.danger-section {
  margin-top: 0.25rem;
}

.danger-action {
  display: flex;
  justify-content: flex-end;
}

.save-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  margin-top: auto;
  padding: 0.75rem 0;
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  background: var(--coar-background-neutral-primary, #fff);
}

.save-status {
  color: var(--coar-text-neutral-tertiary, #6b7280);
  font-size: 0.78rem;
}

.save-status--dirty {
  color: var(--coar-text-semantic-warning, #b45309);
  font-weight: 600;
}

@media (max-width: 900px) {
  .form-grid-2,
  .form-grid-3 { grid-template-columns: 1fr; }
  .rate-limit-grid__header,
  .rate-limit-grid__row { grid-template-columns: minmax(10rem, 1fr) 7rem 8rem; }
}
</style>
