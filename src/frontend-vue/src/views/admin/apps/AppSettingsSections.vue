<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import {
  CoarNotice,
  CoarTextInput, CoarFormField, CoarCheckbox, CoarSelect,
  CoarTabGroup, CoarTab, CoarMultiSelect,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import EditableStringList from '@/components/EditableStringList.vue'
import { useGroupStore } from '@/stores/group.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import { useAppPagesApi, type AppSlotDto } from '@/composables/usePagesApi'
import type { ApplicationSettingsDto } from '@/models/application'

// ADR-0011 per-App settings override sections, extracted from the old standalone
// ApplicationSettingsModal so the single App modal (AppDetails) can carry them as a
// tab. The parent owns load + save; this component owns the form. Populate it via the
// `modelValue` prop (the App's current Settings) and read it back via the exposed
// `build()` — the exact same override/inherit shape the per-App settings doc uses.
const { t } = useI18n()
const props = defineProps<{
  modelValue?: ApplicationSettingsDto | null
  applicationId?: string
  applicationName?: string
}>()

const groupStore = useGroupStore()
const appConfig = useAppConfigStore()
const activeTab = ref<'origin' | 'registration' | 'sessions' | 'grants' | 'oauth' | 'pages'>('origin')

const groupOptions = ref<{ value: string; label: string }[]>([])

// Per-section "override" toggle (on → this App overrides the realm; off → inherit).
// Numbers are kept as strings (empty = inherit that field).
const f = reactive({
  origin: { override: false, subdomain: '' },
  branding: { override: false, productName: '', primaryColor: '' },
  emailBranding: { override: false, productName: '' },
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
  dcr: {
    override: false, enabled: false, access: '', refresh: '',
    reservedNames: [] as string[], perIp: '', perRealm: '',
  },
  cimd: { override: false, enabled: false, access: '', refresh: '' },
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
const inh = computed(() => {
  const r = realmSettingsStore.settings
  return {
    // No realm equivalent — inheriting means "realm primary domain / realm default".
    origin: { subdomain: '' },
    branding: {
      productName: r?.Branding?.ProductName ?? '',
      primaryColor: r?.Branding?.PrimaryColor ?? '',
    },
    emailBranding: { productName: '' },
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
  f.emailBranding.override = false; f.emailBranding.productName = ''
  f.selfReg.override = false; f.selfReg.posture = ''; f.selfReg.enabled = false
  f.selfReg.requireEmailVerification = true; f.selfReg.requireAdminApproval = false
  f.selfReg.allowedEmailDomains = []; f.selfReg.defaultGroupIds = []
  f.selfReg.termsOfServiceUrl = ''; f.selfReg.privacyPolicyUrl = ''
  f.registrationFields.override = false; f.registrationFields.username = ''
  f.registrationFields.firstname = ''; f.registrationFields.lastname = ''
  f.clientSessions.override = false; f.clientSessions.idle = ''; f.clientSessions.absolute = ''
  f.nativeGrants.override = false; f.nativeGrants.enabled = false; f.nativeGrants.access = ''; f.nativeGrants.refresh = ''
  f.dcr.override = false; f.dcr.enabled = false; f.dcr.access = ''; f.dcr.refresh = ''
  f.dcr.reservedNames = []; f.dcr.perIp = ''; f.dcr.perRealm = ''
  f.cimd.override = false; f.cimd.enabled = false; f.cimd.access = ''; f.cimd.refresh = ''
}

function populate(s?: ApplicationSettingsDto | null) {
  resetForm()
  if (!s) return
  if (s.Origin) { f.origin.override = true; f.origin.subdomain = s.Origin.Subdomain ?? '' }
  if (s.Branding) {
    f.branding.override = true
    f.branding.productName = s.Branding.ProductName ?? ''
    f.branding.primaryColor = s.Branding.PrimaryColor ?? ''
  }
  if (s.EmailBranding) {
    f.emailBranding.override = true
    f.emailBranding.productName = s.EmailBranding.ProductName ?? ''
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
}

/** Build the override DTO as the COMPLETE desired state (the App PUT is a replace):
 * an overridden section sends its values, a non-overridden section sends `null` so the
 * backend clears that override (→ inherit the realm). Origin sends null when off
 * (sparse — toggling a subdomain off doesn't clear an existing route in this view). */
function build(): ApplicationSettingsDto {
  return {
    Origin: f.origin.override ? { Subdomain: f.origin.subdomain.trim() || null } : null,
    Branding: f.branding.override
      ? { ProductName: f.branding.productName.trim() || null, PrimaryColor: f.branding.primaryColor.trim() || null }
      : null,
    EmailBranding: f.emailBranding.override
      ? { ProductName: f.emailBranding.productName.trim() || null }
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
  }
}

watch(() => props.modelValue, (s) => populate(s), { immediate: true })
onMounted(async () => {
  await groupStore.initialize()
  groupOptions.value = groupStore.groups.map((g) => ({ value: g.Id, label: g.Name }))
  if (!realmSettingsStore.loaded) await realmSettingsStore.load()
})

defineExpose({ build })

// ── Application page selection (ADR-0001): pick a realm variant per slot ──
const PAGE_SLOT_META = [
  { slug: 'login', label: t('admin.customization.pages.login.title', {}, 'Login') },
  { slug: 'password-forgot', label: t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password') },
  { slug: 'logout', label: t('admin.customization.pages.logout.title', {}, 'Logout') },
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
    <CoarNotice variant="info">
      {{ t('admin.appSettings.hint', {}, 'These settings override the realm defaults only for this app. A disabled section inherits from the realm.') }}
    </CoarNotice>

    <CoarTabGroup v-model="activeTab" class="tab-bar">
      <CoarTab id="origin">{{ t('admin.appSettings.tabs.origin', {}, 'Origin & Branding') }}</CoarTab>
      <CoarTab id="registration">{{ t('admin.appSettings.tabs.registration', {}, 'Registrierung') }}</CoarTab>
      <CoarTab id="sessions">{{ t('admin.appSettings.tabs.sessions', {}, 'Sessions') }}</CoarTab>
      <CoarTab id="grants">{{ t('admin.appSettings.tabs.grants', {}, 'Native Grants') }}</CoarTab>
      <CoarTab id="oauth">{{ t('admin.appSettings.tabs.oauth', {}, 'OAuth (DCR/CIMD)') }}</CoarTab>
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
        <CoarTextInput v-bind="fieldBind('branding', 'primaryColor')" clearable placeholder="#1077be" />
      </CoarFormField>

      <CoarCheckbox v-model="f.emailBranding.override" :label="t('admin.appSettings.email.override', {}, 'Custom Email Branding')" />
      <CoarFormField :label="t('admin.appSettings.email.productName', {}, 'Produktname in E-Mails')">
        <CoarTextInput v-bind="fieldBind('emailBranding', 'productName')" clearable />
      </CoarFormField>
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
</style>
