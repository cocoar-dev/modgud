<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  CoarNotice,
  CoarTextInput,
  CoarPasswordInput,
  CoarNumberInput,
  CoarFormField,
  CoarSelect,
  CoarCheckbox,
  CoarButton,
  CoarIcon,
  CoarPopover,
  CoarDivider,
  CoarTabGroup,
  CoarTab,
  CoarTag,
  CoarDualListbox,
  vTooltip,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import EditableStringList from '@/components/EditableStringList.vue'
import ServiceAccountDetails from '@/views/admin/serviceAccount/ServiceAccountDetails.vue'
import PositionDetails from '@/views/admin/position/PositionDetails.vue'
import { useOAuthClientStore } from '@/stores/oauthClient.store'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import { useServiceAccountStore } from '@/stores/serviceAccount.store'
import { usePositionStore } from '@/stores/position.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useClone, CLIENT_CLONE } from '@/composables/useClone'
import { useDraftStaging } from '@/composables/useDraftStaging'
import type { ManifestEntity } from '@/stores/realmDraft.store'
import { useModalOverlay } from '@/composables/useModalOverlay'
import { MODAL_MD } from '@/router/modal-sizes'
import { useRouter } from 'vue-router'
import type { OAuthClientDto, CreateOAuthClientDto, UpdateOAuthClientDto, AccessTokenType } from '@/models/oauth'
import type { ServiceAccountCreateDto } from '@/models/serviceAccount'
import type { PositionCreateDto } from '@/models/position'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthClientStore()
const scopeStore = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const realmSettingsStore = useRealmSettingsStore()
const serviceAccountStore = useServiceAccountStore()
const positionStore = usePositionStore()
const appConfig = useAppConfigStore()
const modalOverlay = useModalOverlay()
const router = useRouter()
const { consume } = useClone()
const isCreate = computed(() => props.id === 'create' && !justCreated.value)
// Genuinely-existing client opened from the list (drives the regenerate-secret
// affordance) — distinct from the transient just-created state where props.id
// is still 'create' but the client now exists, and from draft-created rows
// (`draft__…`) that exist only in the staged manifest.
const isExistingClient = computed(() => props.id !== 'create' && !props.id.startsWith('draft__'))
const loading = ref(false)
const error = ref<string | null>(null)
// The expert editor exposes the complete object before the first save. General
// and Security are regular tabs instead of a permanently visible side column,
// so every section gets the width it needs without turning the modal into a
// near-full-screen workspace.
type ClientTab = 'general' | 'login' | 'apps' | 'grants' | 'terminal' | 'scopes' | 'urls' | 'lifetimes' | 'security' | 'dcr'
const activeTab = ref<ClientTab>('general')
type UrlListTab = 'redirect' | 'post-logout' | 'cors'
const activeUrlList = ref<UrlListTab>('redirect')

// Cleartext secret returned once at creation / regeneration — surfaced for copy.
const newSecret = ref<string | null>(null)
// Flips true after a successful create so the modal switches from the create
// form to a read-context view of the just-created client (tabs + locked
// identity), letting the admin copy the one-time secret instead of being stuck
// in create shape.
const justCreated = ref(false)

const clientTypeOptions = [
  { value: 'public', label: 'Public' },
  { value: 'confidential', label: 'Confidential' },
]

const consentTypeOptions = [
  { value: 'implicit', label: 'Implicit' },
  { value: 'explicit', label: 'Explicit' },
  { value: 'external', label: 'External' },
]

const accessTokenTypeOptions: { value: AccessTokenType; label: string }[] = [
  { value: 'Jwt', label: 'JWT (self-contained)' },
  { value: 'Reference', label: 'Reference (introspection)' },
]

const lifetimeInputBounds = {
  shortToken: { min: 60, max: 60 * 60, step: 60 },
  authorizationCode: { min: 60, max: 10 * 60, step: 60 },
  refreshToken: { min: 24 * 60 * 60, max: 30 * 24 * 60 * 60, step: 24 * 60 * 60 },
  clientSession: { min: 24 * 60 * 60, max: 3650 * 24 * 60 * 60, step: 24 * 60 * 60 },
} as const

const standardGrantTypeOptions = computed(() => [
  { value: 'authorization_code', label: 'authorization_code',
    subtitle: t('admin.oauthClients.grantTypes.authorizationCodeDescription', {}, 'Interaktiver Benutzer-Flow mit PKCE'),
    icon: 'log-in', group: 'Standard flows' },
  { value: 'refresh_token', label: 'refresh_token',
    subtitle: t('admin.oauthClients.grantTypes.refreshTokenDescription', {}, 'Langlebige Sitzung; erneuert Access-Tokens ohne erneuten Login'),
    icon: 'rotate-ccw', group: 'Standard flows' },
  { value: 'client_credentials', label: 'client_credentials',
    subtitle: t('admin.oauthClients.grantTypes.clientCredentialsDescription', {}, 'Machine-to-Machine ohne Benutzer'),
    icon: 'cpu', group: 'Standard flows' },
  { value: 'urn:ietf:params:oauth:grant-type:device_code', label: 'device_code',
    subtitle: t('admin.oauthClients.grantTypes.deviceCodeDescription', {}, 'Für TVs, CLIs und Geräte ohne eigenen Browser'),
    icon: 'monitor', group: 'Standard flows' },
  // No implicit / password grants: OAuth 2.1 removes both and the backend
  // rejects them (OAuth.UnsupportedGrantType), so they are not offered here.
])

// Native passwordless grants (ADR-0010) — cookieless proofs exchanged
// directly at /connect/token. Only surfaced when the realm has enabled
// native grants (RealmSettings.NativeGrants.Enabled); a client also needs
// the matching gt:urn:cocoar:* permission to actually use them. Existing
// selections are kept visible even when the realm toggle is off so an
// already-configured grant never silently vanishes from the listbox.
const cocoarGrantTypeOptions = computed(() => [
  { value: 'urn:cocoar:otp', label: 'urn:cocoar:otp',
    subtitle: t('admin.oauthClients.grantTypes.otpDescription', {}, 'Einmalcode per E-Mail ohne Browser-Redirect'),
    icon: 'mail', group: 'Native passwordless (Cocoar)' },
  { value: 'urn:cocoar:magic', label: 'urn:cocoar:magic',
    subtitle: t('admin.oauthClients.grantTypes.magicDescription', {}, 'Magic-Link-Token ohne Browser-Redirect'),
    icon: 'link', group: 'Native passwordless (Cocoar)' },
  { value: 'urn:cocoar:passkey', label: 'urn:cocoar:passkey',
    subtitle: t('admin.oauthClients.grantTypes.passkeyDescription', {}, 'WebAuthn-Assertion ohne Browser-Redirect'),
    icon: 'fingerprint', group: 'Native passwordless (Cocoar)' },
])

const nativeGrantsEnabled = computed(
  () => realmSettingsStore.settings?.NativeGrants?.Enabled ?? false)

// The inverse of nativeGrantsEnabled's info note: this client carries a native
// grant but the realm flag is OFF, so the grant silently won't work at
// /connect/token. The grant stays visible (see grantTypeOptions) precisely so an
// existing selection isn't hidden — which is exactly when this warning is needed.
const cocoarGrantValues = new Set(['urn:cocoar:otp', 'urn:cocoar:magic', 'urn:cocoar:passkey'])
const hasNativeGrantWithRealmOff = computed(
  () => !nativeGrantsEnabled.value
    && form.value.AllowedGrantTypes.some((g) => cocoarGrantValues.has(g)))

// MG-FT — staffing is selected like every other grant. The server expands it
// to the fixed technical terminal profile (device_code + refresh_token +
// staffing); the UI keeps the regular picker visible and reports mixed modes
// through the same validation notice used by client_credentials.
const STAFFING_GRANT = 'urn:cocoar:params:oauth:grant-type:staffing'
const DEVICE_CODE_GRANT = 'urn:ietf:params:oauth:grant-type:device_code'
const TERMINAL_GRANT_PROFILE = [DEVICE_CODE_GRANT, 'refresh_token', STAFFING_GRANT] as const
const staffingGrantTypeOptions = computed(() => [
  { value: STAFFING_GRANT, label: 'urn:cocoar:…:staffing',
    subtitle: t('admin.oauthClients.grantTypes.staffingDescription', {}, 'Terminal-Client einer Position — Personal aktiviert per Passkey-Tap'),
    icon: 'briefcase', group: t('admin.oauthClients.grantTypes.groupTerminal', {}, 'Terminal (Positionen)') },
])

const grantTypeOptions = computed(() => {
  const selected = new Set(form.value.AllowedGrantTypes)
  const cocoar = cocoarGrantTypeOptions.value.filter(
    (o) => nativeGrantsEnabled.value || selected.has(o.value))
  const staffing = staffingGrantTypeOptions.value.filter(
    (o) => appConfig.config.Features.PositionTerminals || selected.has(o.value))
  return [...standardGrantTypeOptions.value, ...cocoar, ...staffing]
})

const scopeOptions = computed(() => {
  const standardOidc = new Set(['openid', 'profile', 'email', 'roles', 'permissions', 'offline_access'])
  const standardDescriptions: Record<string, string> = {
    openid: t('admin.oauthClients.scopes.openidDescription', {}, 'Aktiviert OpenID Connect und ID-Tokens'),
    profile: t('admin.oauthClients.scopes.profileDescription', {}, 'Basisprofil wie Name und Anzeigename'),
    email: t('admin.oauthClients.scopes.emailDescription', {}, 'E-Mail-Adresse und Verifizierungsstatus'),
    roles: t('admin.oauthClients.scopes.rolesDescription', {}, 'Rollen des Principals im Token'),
    permissions: t('admin.oauthClients.scopes.permissionsDescription', {}, 'Aufgelöste Berechtigungen im Token'),
    offline_access: t('admin.oauthClients.scopes.offlineAccessDescription', {}, 'Erlaubt die Ausgabe von Refresh-Tokens'),
  }
  return scopeStore.scopes
    .filter((s) => !hasStaffingGrant.value
      || (s.Enabled && s.Resources.length > 0)
      || form.value.Scopes.includes(s.Name))
    .map((s) => {
      const isStandard = standardOidc.has(s.Name)
      const appLabel = s.AppId
        ? applicationsStore.apps.find((a) => a.Id === s.AppId)?.DisplayName ?? s.AppId
        : null
      const resourceLabel = hasStaffingGrant.value && s.Resources.length > 0
        ? `aud: ${s.Resources.join(', ')}`
        : null
      const subtitleParts = [standardDescriptions[s.Name] ?? s.DisplayName, appLabel, resourceLabel].filter(Boolean)
      return {
        value: s.Name,
        label: s.Name,
        subtitle: subtitleParts.length > 0 ? subtitleParts.join(' · ') : undefined,
        icon: 'tag',
        group: isStandard
          ? t('admin.oauthClients.scopes.groupStandard', {}, 'Realm-weit (OIDC-Standard)')
          : appLabel
            ? t('admin.oauthClients.scopes.groupApp', { app: appLabel }, `App: ${appLabel}`)
            : t('admin.oauthClients.scopes.groupRealm', {}, 'Realm-weit'),
      }
    })
})

// App-link DualListbox (n:m). Empty Linked = realm-wide. Multiple
// = Keycloak-style multi-app client (the issued token's resource_access
// claim will carry one entry per linked app).
//
// searchText folds Slug + Description into the searchable text — the
// listbox's default search only walks label/subtitle/group, so without
// this an admin searching by slug (the identity used everywhere else in
// the IdP) gets zero hits when the app has a Description that displaces
// the slug from the subtitle. Worth ~zero perf and removes a real
// friction point reported during cross-app onboarding.
const appOptions = computed(() =>
  applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: a.DisplayName,
    subtitle: a.Description ?? a.Slug,
    icon: a.IsSystem ? 'shield' : 'layout-grid',
    group: a.IsSystem ? 'System apps' : 'User apps',
    searchText: `${a.DisplayName} ${a.Slug} ${a.Description ?? ''}`,
  })),
)

const serviceAccountOptions = computed(() => [
  {
    value: '',
    label: t('admin.oauthClients.serviceAccount.placeholder', {}, 'Service Account wählen…'),
  },
  ...serviceAccountStore.entities
    .filter((sa) => sa.IsActive)
    .map((sa) => ({
      value: sa.Id,
      label: sa.AccountName,
      subtitle: sa.Purpose ?? undefined,
    })),
])

const positionOptions = computed(() => [
  {
    value: '',
    label: t('admin.oauthClients.position.placeholder', {}, 'Position wählen…'),
  },
  ...positionStore.entities
    .filter((position) => position.IsActive && position.TerminalPolicy.Enabled)
    .sort((left, right) => left.AccountName.localeCompare(right.AccountName))
    .map((position) => ({
      value: position.Id,
      label: position.AccountName,
      subtitle: position.Purpose ?? undefined,
    })),
])

const terminalBindingOptions = computed(() => [
  {
    value: 'dpop',
    label: t('admin.positionTerminals.bindingOption.dpop.label', {}, 'DPoP key'),
    subtitle: t('admin.positionTerminals.bindingOption.dpop.hint', {}, 'Device key and sender-constrained tokens'),
  },
  {
    value: 'client-secret',
    label: t('admin.positionTerminals.bindingOption.clientSecret.label', {}, 'Client secret'),
    subtitle: t('admin.positionTerminals.bindingOption.clientSecret.hint', {}, 'Confidential client with a secret shown once'),
  },
  {
    value: 'none',
    label: t('admin.positionTerminals.bindingOption.none.label', {}, 'No device binding'),
    subtitle: t('admin.positionTerminals.bindingOption.none.hint', {}, 'Admin approval only; no verifiable device identity'),
  },
])

interface FormState {
  ClientId: string
  DisplayName: string
  ClientType: string
  ConsentType: string
  ClientSecret: string
  Enabled: boolean
  /** Bare scope names (no `scp:` prefix). Drives `Scopes` on Create/Update. */
  Scopes: string[]
  /** JWT (self-contained, parsed by JwtBearer) vs Reference (needs introspection). */
  AccessTokenType: AccessTokenType
  RedirectUris: string[]
  PostLogoutRedirectUris: string[]
  /** Pre-bound to a multi-select; sent as-is to the backend. */
  AllowedGrantTypes: string[]
  AllowedCorsOrigins: string[]
  /** RFC 9126 — reject this client's direct (non-PAR) /connect/authorize requests. */
  RequirePushedAuthorizationRequests: boolean
  /** RFC 9449 (#118) — require a valid DPoP proof at the token endpoint. */
  RequireDpop: boolean
  /** RFC 9449 §8-9 (#118) — require the client's DPoP proofs to carry a server nonce. */
  RequireDpopNonce: boolean
  IdentityTokenLifetime: number | null
  AccessTokenLifetime: number | null
  AuthorizationCodeLifetime: number | null
  SlidingRefreshTokenLifetime: number | null
  /** Per-client native/OAuth session override in seconds. Null = App/Realm policy. */
  ClientSessionIdleLifetime: number | null
  ClientSessionAbsoluteLifetime: number | null
  /** ADR-0009 — admin-set per-client WebAuthn RP ID for native passkeys. Empty = realm-scoped. */
  WebAuthnRpId: string
  /** Selected App.Ids. Empty list = realm-wide. */
  AppIds: string[]
  /** Required for a pure client_credentials client; immutable after creation. */
  LinkedServiceAccountId: string
  /** Set on terminal-managed clients (read-only viewer; editor = position modal). */
  LinkedPositionPrincipalId: string
  ManagedTerminalEnrollmentId: string
  /** Create-only slot metadata for the staffing terminal profile. */
  TerminalDisplayName: string
  TerminalLocation: string
  TerminalBinding: 'dpop' | 'client-secret' | 'none'
}

const SCOPE_PERMISSION_PREFIX = 'scp:'

/**
 * The backend stores scopes inside OpenIddict's prefixed `Permissions`
 * array (e.g. `"scp:openid"`). Extract the bare names for the form.
 */
function extractScopeNames(permissions: string[] | null | undefined): string[] {
  return (permissions ?? [])
    .filter((p) => p.startsWith(SCOPE_PERMISSION_PREFIX))
    .map((p) => p.slice(SCOPE_PERMISSION_PREFIX.length))
}

function emptyForm(): FormState {
  return {
    ClientId: '',
    DisplayName: '',
    ClientType: 'confidential',
    ConsentType: 'implicit',
    ClientSecret: '',
    Enabled: true,
    Scopes: [],
    // Match the backend contract and documented security default. The expert
    // editor still exposes the choice explicitly under Tokens & Sessions.
    AccessTokenType: 'Reference',
    RedirectUris: [],
    PostLogoutRedirectUris: [],
    AllowedGrantTypes: [],
    AllowedCorsOrigins: [],
    RequirePushedAuthorizationRequests: false,
    RequireDpop: false,
    RequireDpopNonce: false,
    IdentityTokenLifetime: null,
    AccessTokenLifetime: null,
    AuthorizationCodeLifetime: null,
    SlidingRefreshTokenLifetime: null,
    ClientSessionIdleLifetime: null,
    ClientSessionAbsoluteLifetime: null,
    WebAuthnRpId: '',
    AppIds: [],
    LinkedServiceAccountId: '',
    LinkedPositionPrincipalId: '',
    ManagedTerminalEnrollmentId: '',
    TerminalDisplayName: '',
    TerminalLocation: '',
    TerminalBinding: 'dpop',
  }
}

const form = ref<FormState>(emptyForm())
const original = ref<OAuthClientDto | null>(null)
const useNewServiceAccountDraft = ref(false)
const newServiceAccountForm = ref({
  AccountName: '',
  Purpose: '',
  IsActive: true,
})
const serviceAccountNamePattern = /^[a-z0-9][a-z0-9._-]{1,63}$/

const newServiceAccountNameError = computed(() => {
  if (!useNewServiceAccountDraft.value) return ''
  const value = newServiceAccountForm.value.AccountName.trim()
  if (!value) return ''
  if (!serviceAccountNamePattern.test(value))
    return t(
      'admin.oauthClients.newServiceAccount.invalidName',
      {},
      '2–64 Zeichen; nur Kleinbuchstaben, Ziffern, Punkt, Bindestrich und Unterstrich.',
    )
  return ''
})

async function openNewServiceAccountModal() {
  const initial: ServiceAccountCreateDto | undefined = useNewServiceAccountDraft.value
    ? {
        AccountName: newServiceAccountForm.value.AccountName,
        Purpose: newServiceAccountForm.value.Purpose || undefined,
        IsActive: newServiceAccountForm.value.IsActive,
      }
    : undefined
  const draft = await modalOverlay.open<ServiceAccountCreateDto>(
    ServiceAccountDetails,
    MODAL_MD,
    { id: 'create', draftOnly: true, initial },
  )
  if (!draft) return

  form.value.LinkedServiceAccountId = ''
  newServiceAccountForm.value = {
    AccountName: draft.AccountName,
    Purpose: draft.Purpose ?? '',
    IsActive: draft.IsActive ?? true,
  }
  useNewServiceAccountDraft.value = true
}

function discardNewServiceAccountDraft() {
  useNewServiceAccountDraft.value = false
  newServiceAccountForm.value = { AccountName: '', Purpose: '', IsActive: true }
}

// ── Terminal client (MG-FT) ───────────────────────────────────────────────
// In create mode the staffing grant opens the same choose-or-create ownership
// pattern as client_credentials + ServiceAccount. Persisted terminal clients
// keep their lifecycle fields locked, while their business-resource access is
// edited through the dedicated terminal-owned endpoint.
const hasStaffingGrant = computed(() => form.value.AllowedGrantTypes.includes(STAFFING_GRANT))
const newPositionDraft = ref<PositionCreateDto | null>(null)
const terminalProfileSnapshot = ref<Partial<FormState> | null>(null)

const selectedPosition = computed(() => positionStore.entities.find(
  (position) => position.Id === form.value.LinkedPositionPrincipalId))

const selectedPositionAllowsBinding = computed(() => !selectedPosition.value
  || selectedPosition.value.TerminalPolicy.AllowedDeviceBindings.includes(form.value.TerminalBinding))

const newPositionAllowsBinding = computed(() => !newPositionDraft.value
  || (newPositionDraft.value.TerminalPolicy?.AllowedDeviceBindings ?? []).includes(form.value.TerminalBinding))

async function openNewPositionModal() {
  const initial: PositionCreateDto = newPositionDraft.value ?? {
    AccountName: '',
    Purpose: undefined,
    IsActive: true,
    TerminalPolicy: {
      Enabled: true,
      AllowedActivationProofs: ['personal-passkey'],
      AllowedDeviceBindings: [form.value.TerminalBinding],
      StaffingSessionLifetimeMinutes: 16 * 60,
      MaximumStaffingSessionLifetimeMinutes: 24 * 60,
    },
  }
  const draft = await modalOverlay.open<PositionCreateDto>(
    PositionDetails,
    MODAL_MD,
    { id: 'create', draftOnly: true, initial },
  )
  if (!draft) return

  form.value.LinkedPositionPrincipalId = ''
  newPositionDraft.value = draft
}

function discardNewPositionDraft() {
  newPositionDraft.value = null
}

watch(hasStaffingGrant, (enabled, wasEnabled) => {
  if (!isCreate.value) return
  if (!enabled) {
    if (activeTab.value === 'terminal') activeTab.value = 'grants'
    // Selecting Staffing temporarily replaces only the profile-owned values.
    // Restore the admin's previous draft if they remove the grant again.
    if (wasEnabled && terminalProfileSnapshot.value)
      Object.assign(form.value, terminalProfileSnapshot.value)
    terminalProfileSnapshot.value = null
    return
  }

  if (!wasEnabled) {
    terminalProfileSnapshot.value = {
      ClientType: form.value.ClientType,
      ClientSecret: form.value.ClientSecret,
      ConsentType: form.value.ConsentType,
      Enabled: form.value.Enabled,
      Scopes: [...form.value.Scopes],
      AccessTokenType: form.value.AccessTokenType,
      RedirectUris: [...form.value.RedirectUris],
      PostLogoutRedirectUris: [...form.value.PostLogoutRedirectUris],
      AllowedCorsOrigins: [...form.value.AllowedCorsOrigins],
      RequirePushedAuthorizationRequests: form.value.RequirePushedAuthorizationRequests,
      RequireDpop: form.value.RequireDpop,
      RequireDpopNonce: form.value.RequireDpopNonce,
      IdentityTokenLifetime: form.value.IdentityTokenLifetime,
      AccessTokenLifetime: form.value.AccessTokenLifetime,
      AuthorizationCodeLifetime: form.value.AuthorizationCodeLifetime,
      SlidingRefreshTokenLifetime: form.value.SlidingRefreshTokenLifetime,
      ClientSessionIdleLifetime: form.value.ClientSessionIdleLifetime,
      ClientSessionAbsoluteLifetime: form.value.ClientSessionAbsoluteLifetime,
      AppIds: [...form.value.AppIds],
    }
  }

  // The selected grant itself remains in the ordinary dual list. Only values
  // that the terminal profile truly owns are normalized and locked.
  Object.assign(form.value, {
    ClientType: form.value.TerminalBinding === 'client-secret' ? 'confidential' : 'public',
    ClientSecret: '',
    ConsentType: 'implicit',
    Enabled: true,
    AccessTokenType: 'Reference',
    RedirectUris: [],
    PostLogoutRedirectUris: [],
    AllowedCorsOrigins: [],
    RequirePushedAuthorizationRequests: false,
    RequireDpop: form.value.TerminalBinding === 'dpop',
    RequireDpopNonce: false,
    IdentityTokenLifetime: null,
    AccessTokenLifetime: null,
    AuthorizationCodeLifetime: null,
    SlidingRefreshTokenLifetime: null,
    ClientSessionIdleLifetime: null,
    ClientSessionAbsoluteLifetime: null,
  })
})

watch(() => form.value.TerminalBinding, (binding) => {
  if (!hasStaffingGrant.value || !isCreate.value) return
  form.value.ClientType = binding === 'client-secret' ? 'confidential' : 'public'
  form.value.RequireDpop = binding === 'dpop'
  form.value.RequireDpopNonce = false
  if (binding !== 'client-secret') form.value.ClientSecret = ''
})

const isTerminalManaged = computed(() => !isCreate.value
  && (!!form.value.ManagedTerminalEnrollmentId || !!form.value.LinkedPositionPrincipalId))

// ── ADR-0005 staging: ordinary client saves commit onto the active draft. The
// manifest deliberately does NOT model SA-linked (client_credentials),
// terminal-/staffing-managed or just-created clients — those keep the live
// path, exactly like the exporter skips them.
const staging = useDraftStaging('clients')
const isDraftRow = computed(() => staging.isDraftId(props.id))
const stagedSave = computed(() => staging.stagingActive.value
  && !justCreated.value
  && !isTerminalManaged.value
  && !hasStaffingGrant.value
  && !form.value.LinkedServiceAccountId
  && !useNewServiceAccountDraft.value
  && !form.value.AllowedGrantTypes.includes('client_credentials'))

function appSlugsOf(appIds: string[]): string[] {
  return appIds
    .map((id) => applicationsStore.apps.find((a) => a.Id === id)?.Slug)
    .filter((s): s is string => !!s)
}

function appIdsOf(slugs: unknown): string[] {
  if (!Array.isArray(slugs)) return []
  return (slugs as unknown[])
    .map((slug) => applicationsStore.apps.find((a) => a.Slug === slug)?.Id)
    .filter((id): id is string => !!id)
}

function fromStaged(e: ManifestEntity): FormState {
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  const arr = (v: unknown) => (Array.isArray(v) ? [...(v as string[])] : [])
  const num = (v: unknown) => (typeof v === 'number' ? v : null)
  return {
    ...emptyForm(),
    ClientId: str(e.ClientId),
    DisplayName: str(e.DisplayName),
    ClientType: str(e.ClientType) || 'confidential',
    ConsentType: str(e.ConsentType) || 'implicit',
    Enabled: e.Enabled !== false,
    Scopes: arr(e.Scopes),
    AccessTokenType: (str(e.AccessTokenType) || 'Reference') as AccessTokenType,
    RedirectUris: arr(e.RedirectUris),
    PostLogoutRedirectUris: arr(e.PostLogoutRedirectUris),
    AllowedGrantTypes: arr(e.AllowedGrantTypes),
    AllowedCorsOrigins: arr(e.AllowedCorsOrigins),
    RequirePushedAuthorizationRequests: e.RequirePushedAuthorizationRequests === true,
    RequireDpop: e.RequireDpop === true,
    RequireDpopNonce: e.RequireDpopNonce === true,
    IdentityTokenLifetime: num(e.IdentityTokenLifetime),
    AccessTokenLifetime: num(e.AccessTokenLifetime),
    AuthorizationCodeLifetime: num(e.AuthorizationCodeLifetime),
    SlidingRefreshTokenLifetime: num(e.SlidingRefreshTokenLifetime),
    ClientSessionIdleLifetime: num(e.ClientSessionIdleLifetime),
    ClientSessionAbsoluteLifetime: num(e.ClientSessionAbsoluteLifetime),
    WebAuthnRpId: str(e.WebAuthnRpId),
    AppIds: appIdsOf(e.Apps),
  }
}

/** Manifest client entity from the form, merged over the already-staged entity
 * so manifest fields the modal doesn't edit (Roles, Claims, …) survive. A null
 * optional drops the key = "no change" on apply (the manifest cannot CLEAR a
 * stored lifetime — that stays a live admin-API operation). */
function toStaged(): ManifestEntity {
  const key = form.value.ClientId.trim()
  const entity: ManifestEntity = { ...(staging.findStaged(key) ?? {}) }
  entity.ClientId = key
  entity.ClientType = form.value.ClientType
  entity.ConsentType = form.value.ConsentType
  entity.Enabled = form.value.Enabled
  entity.Scopes = [...form.value.Scopes]
  entity.RedirectUris = [...form.value.RedirectUris]
  entity.PostLogoutRedirectUris = [...form.value.PostLogoutRedirectUris]
  entity.AllowedGrantTypes = [...form.value.AllowedGrantTypes]
  entity.AllowedCorsOrigins = [...form.value.AllowedCorsOrigins]
  entity.AccessTokenType = form.value.AccessTokenType
  entity.RequirePushedAuthorizationRequests = form.value.RequirePushedAuthorizationRequests
  entity.RequireDpop = form.value.RequireDpop
  entity.RequireDpopNonce = form.value.RequireDpopNonce
  entity.Apps = appSlugsOf(form.value.AppIds)
  // v2 merge-patch: the modal edits all of these, so stage each one explicitly —
  // null IS the payload for "cleared" (an absent field would mean "unchanged"
  // and silently keep whatever the live value is at apply time).
  entity.DisplayName = form.value.DisplayName.trim() || null
  entity.WebAuthnRpId = form.value.WebAuthnRpId.trim() || null
  entity.IdentityTokenLifetime = form.value.IdentityTokenLifetime
  entity.AccessTokenLifetime = form.value.AccessTokenLifetime
  entity.AuthorizationCodeLifetime = form.value.AuthorizationCodeLifetime
  entity.SlidingRefreshTokenLifetime = form.value.SlidingRefreshTokenLifetime
  entity.ClientSessionIdleLifetime = form.value.ClientSessionIdleLifetime
  entity.ClientSessionAbsoluteLifetime = form.value.ClientSessionAbsoluteLifetime
  // Create-only explicit secret — the server strips it into a write-only
  // DataProtection slot on save; existing clients keep their stored secret.
  const secret = form.value.ClientSecret.trim()
  if (secret) entity.ClientSecret = secret
  // Stage the LIVE entity's id: the apply matches by identity, so editing the name
  // is a RENAME of this entity instead of staging a second one.
  if (!isCreate.value && !isDraftRow.value) entity.Id = props.id
  return entity
}

function goToPosition() {
  // Deliberately NOT props.close(): the routed-modal plumbing reacts to a
  // resolved close by pushing the list route again, which would clobber this
  // navigation. Changing the route unmounts the client list, which closes
  // this modal itself, and the fragment opens the position modal over there.
  void router.push(`/admin/positions#${form.value.LinkedPositionPrincipalId}`)
}

function fromDto(dto: OAuthClientDto): FormState {
  return {
    ClientId: dto.ClientId,
    DisplayName: dto.DisplayName ?? '',
    ClientType: dto.ClientType,
    ConsentType: dto.ConsentType,
    ClientSecret: '',
    Enabled: dto.Enabled,
    Scopes: extractScopeNames(dto.Permissions),
    AccessTokenType: (dto.AccessTokenType as AccessTokenType) ?? 'Reference',
    RedirectUris: [...(dto.RedirectUris ?? [])],
    PostLogoutRedirectUris: [...(dto.PostLogoutRedirectUris ?? [])],
    AllowedGrantTypes: [...(dto.AllowedGrantTypes ?? [])],
    AllowedCorsOrigins: [...(dto.AllowedCorsOrigins ?? [])],
    RequirePushedAuthorizationRequests: dto.RequirePushedAuthorizationRequests,
    RequireDpop: dto.RequireDpop,
    RequireDpopNonce: dto.RequireDpopNonce,
    IdentityTokenLifetime: dto.IdentityTokenLifetime ?? null,
    AccessTokenLifetime: dto.AccessTokenLifetime ?? null,
    AuthorizationCodeLifetime: dto.AuthorizationCodeLifetime ?? null,
    SlidingRefreshTokenLifetime: dto.SlidingRefreshTokenLifetime ?? null,
    ClientSessionIdleLifetime: dto.ClientSessionIdleLifetime ?? null,
    ClientSessionAbsoluteLifetime: dto.ClientSessionAbsoluteLifetime ?? null,
    WebAuthnRpId: dto.WebAuthnRpId ?? '',
    AppIds: [...(dto.AppIds ?? [])],
    LinkedServiceAccountId: dto.LinkedServiceAccountId ?? '',
    LinkedPositionPrincipalId: dto.LinkedPositionPrincipalId ?? '',
    ManagedTerminalEnrollmentId: dto.ManagedTerminalEnrollmentId ?? '',
    TerminalDisplayName: '',
    TerminalLocation: '',
    TerminalBinding: dto.ClientType === 'confidential' ? 'client-secret' : dto.RequireDpop ? 'dpop' : 'none',
  }
}

const modalTitle = computed(() => {
  if (isCreate.value) return t('admin.oauthClients.createTitle', {}, 'Create OAuth Client')
  return form.value.DisplayName || form.value.ClientId
})

const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.ClientId)

const terminalBindingMeetsRealmFloor = computed(() => {
  const required = realmSettingsStore.settings?.PositionSecurity?.RequiredBindingCapabilities ?? []
  if (required.includes('SenderConstrained') && form.value.TerminalBinding !== 'dpop') return false
  if (required.includes('DeviceIdentity') && form.value.TerminalBinding === 'none') return false
  return true
})

// Create-time guard rails: a client with no grants can't mint any token, and an
// authorization_code client with no redirect URI can't complete the flow. Block
// Create until these are satisfied so the admin can't silently produce a dead
// client (the original bug: create only exposed identity fields).
const flowIssues = computed<string[]>(() => {
  if (!isCreate.value) return []
  const errs: string[] = []
  const grants = form.value.AllowedGrantTypes
  const hasClientCredentials = grants.includes('client_credentials')
  const hasUserFlow = grants.some((grant) => grant !== 'client_credentials')
  if (form.value.AllowedGrantTypes.length === 0)
    errs.push(t('admin.oauthClients.validation.noFlows', {}, 'Mindestens einen Grant Type wählen — ohne Grant kann der Client keine Tokens ausstellen.'))
  if (hasClientCredentials && hasUserFlow)
    errs.push(t('admin.oauthClients.validation.mixedGrantModes', {}, 'client_credentials kann nicht mit User-Flows kombiniert werden. Lege dafür einen eigenen Machine-Client an.'))
  if (hasClientCredentials && useNewServiceAccountDraft.value && !newServiceAccountForm.value.AccountName.trim())
    errs.push(t('admin.oauthClients.validation.newServiceAccountNameRequired', {}, 'Für den neuen Service Account ist ein Account-Name erforderlich.'))
  else if (hasClientCredentials && useNewServiceAccountDraft.value && newServiceAccountNameError.value)
    errs.push(t('admin.oauthClients.validation.newServiceAccountNameInvalid', {}, 'Der Account-Name des neuen Service Accounts ist ungültig.'))
  else if (hasClientCredentials && !form.value.LinkedServiceAccountId && !useNewServiceAccountDraft.value)
    errs.push(t('admin.oauthClients.validation.noServiceAccount', {}, 'client_credentials benötigt einen Service Account.'))
  if (hasStaffingGrant.value && grants.some((grant) => grant !== STAFFING_GRANT))
    errs.push(t('admin.oauthClients.validation.invalidTerminalGrants', {}, 'staffing kann nicht mit anderen Grants kombiniert werden. device_code und refresh_token werden beim Erstellen technisch ergänzt.'))
  return errs
})

const terminalIssues = computed<string[]>(() => {
  if (!hasStaffingGrant.value) return []
  const errs: string[] = []
  if (isCreate.value) {
    if (!form.value.LinkedPositionPrincipalId && !newPositionDraft.value)
      errs.push(t('admin.oauthClients.validation.noPosition', {}, 'Das Terminalprofil benötigt eine bestehende oder neue Position.'))
    if (!form.value.TerminalDisplayName.trim())
      errs.push(t('admin.oauthClients.validation.noTerminalName', {}, 'Für den Terminal-Slot ist ein Anzeigename erforderlich.'))
    if (!form.value.WebAuthnRpId.trim())
      errs.push(t('admin.oauthClients.validation.noTerminalRpId', {}, 'Für Personal-Passkeys ist eine WebAuthn RP-ID erforderlich.'))
    if (selectedPosition.value && !selectedPositionAllowsBinding.value)
      errs.push(t('admin.oauthClients.validation.positionBindingMismatch', {}, 'Die gewählte Position erlaubt diese Gerätebindung nicht.'))
    if (newPositionDraft.value && !newPositionDraft.value.AccountName.trim())
      errs.push(t('admin.oauthClients.validation.newPositionNameRequired', {}, 'Für die neue Position ist ein Account-Name erforderlich.'))
    else if (newPositionDraft.value && !serviceAccountNamePattern.test(newPositionDraft.value.AccountName.trim()))
      errs.push(t('admin.oauthClients.validation.newPositionNameInvalid', {}, 'Der Account-Name der neuen Position ist ungültig.'))
    if (newPositionDraft.value && newPositionDraft.value.TerminalPolicy?.Enabled !== true)
      errs.push(t('admin.oauthClients.validation.newPositionTerminalsDisabled', {}, 'Bei der neuen Position muss die Terminalnutzung aktiviert sein.'))
    if (!newPositionAllowsBinding.value)
      errs.push(t('admin.oauthClients.validation.newPositionBindingMismatch', {}, 'Die neue Position erlaubt die gewählte Gerätebindung nicht.'))
    if (!terminalBindingMeetsRealmFloor.value)
      errs.push(t('admin.oauthClients.validation.terminalBindingBelowRealmFloor', {}, 'Die Gerätebindung erfüllt die Sicherheitsvorgabe des Realms nicht.'))
  }
  for (const scopeName of form.value.Scopes) {
    const scope = scopeStore.scopes.find((candidate) => candidate.Name === scopeName)
    if (scope?.AppId && !form.value.AppIds.includes(scope.AppId))
      errs.push(t('admin.oauthClients.validation.terminalScopeAppMissing', { scope: scopeName }, `Für Scope ${scopeName} muss dessen App ausgewählt sein.`))
  }
  return errs
})

const redirectIssues = computed<string[]>(() => {
  if (!isCreate.value) return []
  if (form.value.AllowedGrantTypes.includes('authorization_code') && form.value.RedirectUris.length === 0)
    return [t('admin.oauthClients.validation.noAuthorizationCodeRedirect', {}, 'authorization_code benötigt mindestens eine Redirect-URI.')]
  return []
})

const createBlockers = computed(() => [...flowIssues.value, ...terminalIssues.value, ...redirectIssues.value])

const footerButton = computed(() => {
  // After a successful create we only offer "Done" (copy-the-secret then close)
  // — the client already exists, and saving again would target the wrong id.
  if (justCreated.value)
    return {
      visible: true,
      text: t('common.done', {}, 'Done'),
      disabled: false,
      loading: false,
      onClick: () => props.close(),
    }
  if (isTerminalManaged.value)
    return {
      visible: true,
      text: t('common.save', {}, 'Save'),
      disabled: loading.value || terminalIssues.value.length > 0,
      loading: loading.value,
      onClick: save,
    }
  return {
    visible: true,
    text: stagedSave.value
      ? t('admin.realmConfig.entry.save', {}, 'In den Draft übernehmen')
      : isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
    disabled: !form.value.ClientId.trim() || loading.value || createBlockers.value.length > 0,
    loading: loading.value,
    onClick: save,
  }
})

onMounted(async () => {
  // Apps for the App-link dropdown + scopes for the Allowed-Scopes
  // multiselect — needed for both create + edit.
  applicationsStore.initialize()
  scopeStore.initialize()
  serviceAccountStore.initialize()
  // Realm settings gate whether the native passwordless grants appear in the
  // grant-type picker. Cheap singleton GET; skip if already in the store.
  if (!realmSettingsStore.loaded) realmSettingsStore.load().catch(() => {})
  if (appConfig.config.Features.PositionTerminals && positionStore.entities.length === 0)
    positionStore.loadAll().catch(() => {})
  if (isDraftRow.value) {
    // Draft-created client: the staged manifest entity IS the state. Apps must
    // be loaded before slugs can resolve back to App ids.
    await applicationsStore.initialize()
    const entity = staging.findStaged(staging.draftKeyOf(props.id))
    if (entity) form.value = fromStaged(entity)
    return
  }
  if (isCreate.value) {
    // Clone: prefill the whole form (ClientId blank, secret dropped → a fresh
    // one is minted on create).
    const clone = consume<OAuthClientDto>(CLIENT_CLONE.entity)
    if (clone) form.value = fromDto(clone)
    return
  }
  loading.value = true
  try {
    const dto = await store.loadOne(props.id)
    if (!dto) {
      error.value = t('admin.oauthClients.loadFailed', {}, 'Failed to load the client.')
      return
    }
    original.value = dto
    form.value = fromDto(dto)
    // Staging overlay: show the STAGED client state when the draft carries it.
    // stagedSave re-evaluates on the loaded form, so SA-/terminal-linked
    // clients (live path) never get overlaid.
    if (stagedSave.value && staging.draftStore.current) {
      const entity = staging.findStaged(dto.ClientId)
      if (entity) {
        await applicationsStore.initialize()
        form.value = fromStaged(entity)
      }
    }
  } finally {
    loading.value = false
  }
})

async function save() {
  if (!form.value.ClientId.trim()) return
  loading.value = true
  error.value = null
  try {
    // ADR-0005: commit onto the active draft instead of writing live.
    if (stagedSave.value) {
      await staging.stage(form.value.ClientId.trim(), toStaged())
      props.close()
      return
    }
    if (isCreate.value) {
      const created = await store.create(buildCreateDto())
      if (created.CreatedServiceAccount)
        serviceAccountStore.setStoreEntities([created.CreatedServiceAccount])
      if (created.CreatedPosition)
        positionStore.setStoreEntities([created.CreatedPosition])
      newSecret.value = created.ClientSecret ?? null
      if (newSecret.value) {
        // Keep the modal open so the admin can copy the cleartext secret, and
        // flip to the read-context view (tabs + locked identity) of the
        // just-created client instead of staying in create shape.
        original.value = created.Client
        form.value = fromDto(created.Client)
        justCreated.value = true
      } else {
        props.close()
      }
    } else if (isTerminalManaged.value && form.value.ManagedTerminalEnrollmentId) {
      await store.updateTerminalAccess(props.id, form.value.ManagedTerminalEnrollmentId, {
        DisplayName: form.value.DisplayName.trim() || null,
        Scopes: [...form.value.Scopes],
        AppIds: [...form.value.AppIds],
      })
      props.close()
    } else {
      await store.update(props.id, buildUpdateDto())
      props.close()
    }
  } catch (e: any) {
    error.value = e?.body?.error ?? e?.body?.detail ?? e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

function buildCreateDto(): CreateOAuthClientDto {
  const dto: CreateOAuthClientDto = {
    ClientId: form.value.ClientId.trim(),
    ClientType: form.value.ClientType,
    DisplayName: form.value.DisplayName.trim() || null,
    ConsentType: form.value.ConsentType,
    Enabled: form.value.Enabled,
    Scopes: [...form.value.Scopes],
    AccessTokenType: form.value.AccessTokenType,
    RequirePushedAuthorizationRequests: form.value.RequirePushedAuthorizationRequests,
    RequireDpop: form.value.RequireDpop,
    RequireDpopNonce: form.value.RequireDpopNonce,
    IdentityTokenLifetime: form.value.IdentityTokenLifetime,
    AccessTokenLifetime: form.value.AccessTokenLifetime,
    AuthorizationCodeLifetime: form.value.AuthorizationCodeLifetime,
    SlidingRefreshTokenLifetime: form.value.SlidingRefreshTokenLifetime,
    ClientSessionIdleLifetime: form.value.ClientSessionIdleLifetime,
    ClientSessionAbsoluteLifetime: form.value.ClientSessionAbsoluteLifetime,
    RedirectUris: [...form.value.RedirectUris],
    PostLogoutRedirectUris: [...form.value.PostLogoutRedirectUris],
    AllowedGrantTypes: [...form.value.AllowedGrantTypes],
    AllowedCorsOrigins: [...form.value.AllowedCorsOrigins],
  }
  const secret = form.value.ClientSecret.trim()
  if (secret) dto.ClientSecret = secret
  const rpId = form.value.WebAuthnRpId.trim()
  if (rpId) dto.WebAuthnRpId = rpId
  if (form.value.AppIds.length > 0) dto.AppIds = [...form.value.AppIds]
  if (form.value.AllowedGrantTypes.includes('client_credentials')) {
    if (useNewServiceAccountDraft.value) {
      dto.NewServiceAccount = {
        AccountName: newServiceAccountForm.value.AccountName.trim(),
        Purpose: newServiceAccountForm.value.Purpose.trim() || undefined,
        IsActive: newServiceAccountForm.value.IsActive,
      }
    } else if (form.value.LinkedServiceAccountId) {
      dto.LinkedServiceAccountId = form.value.LinkedServiceAccountId
    }
  }
  if (hasStaffingGrant.value) {
    // Keep the wire request explicit even though the server pins this profile
    // independently. This prevents UI changes from accidentally reintroducing
    // a mixed-grant terminal client.
    dto.AllowedGrantTypes = [...TERMINAL_GRANT_PROFILE]
    dto.ClientType = form.value.TerminalBinding === 'client-secret' ? 'confidential' : 'public'
    dto.AccessTokenType = 'Reference'
    dto.RequireDpop = form.value.TerminalBinding === 'dpop'
    dto.RequireDpopNonce = false
    dto.LinkedPositionPrincipalId = form.value.LinkedPositionPrincipalId || undefined
    dto.NewPosition = newPositionDraft.value
      ? {
          AccountName: newPositionDraft.value.AccountName.trim(),
          Purpose: newPositionDraft.value.Purpose?.trim() || undefined,
          IsActive: newPositionDraft.value.IsActive ?? true,
          TerminalPolicy: newPositionDraft.value.TerminalPolicy
            ? {
                ...newPositionDraft.value.TerminalPolicy,
                AllowedActivationProofs: [...(newPositionDraft.value.TerminalPolicy.AllowedActivationProofs ?? [])],
                AllowedDeviceBindings: [...(newPositionDraft.value.TerminalPolicy.AllowedDeviceBindings ?? [])],
              }
            : undefined,
        }
      : undefined
    dto.TerminalDisplayName = form.value.TerminalDisplayName.trim()
    dto.TerminalLocation = form.value.TerminalLocation.trim() || undefined
    dto.TerminalBinding = form.value.TerminalBinding
  }
  return dto
}

function buildUpdateDto(): UpdateOAuthClientDto {
  return {
    DisplayName: form.value.DisplayName.trim() || null,
    ConsentType: form.value.ConsentType,
    Enabled: form.value.Enabled,
    Scopes: [...form.value.Scopes],
    AccessTokenType: form.value.AccessTokenType,
    RedirectUris: [...form.value.RedirectUris],
    PostLogoutRedirectUris: [...form.value.PostLogoutRedirectUris],
    AllowedGrantTypes: [...form.value.AllowedGrantTypes],
    AllowedCorsOrigins: [...form.value.AllowedCorsOrigins],
    RequirePushedAuthorizationRequests: form.value.RequirePushedAuthorizationRequests,
    RequireDpop: form.value.RequireDpop,
    RequireDpopNonce: form.value.RequireDpopNonce,
    IdentityTokenLifetime: form.value.IdentityTokenLifetime,
    AccessTokenLifetime: form.value.AccessTokenLifetime,
    AuthorizationCodeLifetime: form.value.AuthorizationCodeLifetime,
    SlidingRefreshTokenLifetime: form.value.SlidingRefreshTokenLifetime,
    // v2 merge-patch: an explicit null in the JSON clears the stored override
    // (an empty lifetime field falls back to the server default).
    ClientSessionIdleLifetime: form.value.ClientSessionIdleLifetime,
    ClientSessionAbsoluteLifetime: form.value.ClientSessionAbsoluteLifetime,
    // ADR-0009 PATCH: null clears back to realm-scoped, a host sets the
    // per-client RP ID.
    WebAuthnRpId: form.value.WebAuthnRpId.trim() || null,
    // Always send AppIds on update — empty array = detach all, otherwise replace.
    AppIds: [...form.value.AppIds],
  }
}

async function regenerateSecret() {
  if (isCreate.value) return
  if (!confirm(t('admin.oauthClients.confirmRegen', {}, 'Really regenerate? The old secret becomes invalid immediately.'))) return
  loading.value = true
  try {
    const res = await store.regenerateSecret(props.id)
    newSecret.value = res.ClientSecret
  } catch (e: any) {
    error.value = e?.body?.error ?? e?.body?.detail ?? e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

async function copySecret() {
  if (!newSecret.value) return
  try { await navigator.clipboard.writeText(newSecret.value) } catch { /* ignore */ }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="app-window"
    :footer-button="footerButton">
    <div v-if="loading && !original && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
    <div v-else class="modal-body">
      <!-- New-secret notice — full-width across both columns -->
      <CoarNotice v-if="newSecret" variant="warning" class="secret-banner">
        <div class="flex flex-col gap-2">
          <div class="font-medium">{{ t('admin.oauthClients.secretOnce', {}, 'Please copy the client secret now — it won\'t be shown again.') }}</div>
          <div class="flex items-center gap-2">
            <code class="flex-1 break-all rounded bg-white/40 px-2 py-1 text-xs">{{ newSecret }}</code>
            <CoarButton size="s" variant="secondary" icon-start="copy" @click="copySecret">
              {{ t('common.copy', {}, 'Copy') }}
            </CoarButton>
          </div>
        </div>
      </CoarNotice>

      <!-- Server / validation error — surfaced at the top of the modal so it is
           visible regardless of the active tab or scroll position. The server
           sends an actionable message (e.g. "client_credentials must be linked
           to a ServiceAccount"); show it instead of a bare "HTTP 400". -->
      <CoarNotice v-if="error" variant="error" class="secret-banner">
        {{ error }}
      </CoarNotice>

      <!-- Terminal lifecycle stays position-owned. The OAuth resource profile
           (Display Name, Apps, Scopes) remains editable here. -->
      <CoarNotice v-if="isTerminalManaged" truncate variant="info" class="secret-banner">
        <div class="flex items-center gap-3">
          <span class="min-w-0 flex-1">
            {{ t('admin.oauthClients.terminal.managedHintShort', {}, 'Terminal-Lifecycle und Sicherheitsprofil werden über Positionen verwaltet.') }}
          </span>
          <CoarButton v-if="form.LinkedPositionPrincipalId" size="s" variant="secondary" icon-start="briefcase" class="shrink-0" @click="goToPosition">
            {{ t('admin.oauthClients.terminal.goToPosition', {}, 'Zur Position') }}
          </CoarButton>
        </div>
        <template #details>
          {{ t('admin.oauthClients.terminal.managedHint', {}, 'Display Name, Apps und Scopes können hier geändert werden. Grants, Gerätebindung, RP-ID und Lifecycle bleiben an den Terminal-Slot gebunden.') }}
        </template>
      </CoarNotice>

      <!-- Full-object expert editor. Every tab participates in the same local
           draft and the complete DTO is persisted by the single footer action.
           Nothing has to be created first and completed in a second pass. -->
      <section class="client-editor">
        <CoarTabGroup v-model="activeTab" class="tab-bar">
          <CoarTab id="general">{{ t('admin.oauthClients.tabs.general', {}, 'General') }}</CoarTab>
          <CoarTab id="login">{{ t('admin.oauthClients.tabs.loginAndConsent', {}, 'Login & Zustimmung') }}</CoarTab>
          <CoarTab id="apps">{{ t('admin.oauthClients.tabs.apps', {}, 'Apps') }}</CoarTab>
          <CoarTab id="grants">
            <span class="tab-label">
              {{ t('admin.oauthClients.tabs.flows', {}, 'Flows') }}
              <span
                v-if="flowIssues.length"
                v-tooltip="{ content: flowIssues.join(' · '), placement: 'bottom' }"
                class="tab-issue"
                role="img"
                :aria-label="flowIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
            </span>
          </CoarTab>
          <CoarTab v-if="hasStaffingGrant" id="terminal">
            <span class="tab-label">
              {{ t('admin.oauthClients.tabs.terminal', {}, 'Terminal') }}
              <span
                v-if="terminalIssues.length"
                v-tooltip="{ content: terminalIssues.join(' · '), placement: 'bottom' }"
                class="tab-issue"
                role="img"
                :aria-label="terminalIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
            </span>
          </CoarTab>
          <CoarTab id="scopes">{{ t('admin.oauthClients.tabs.scopes', {}, 'Scopes') }}</CoarTab>
          <CoarTab id="urls">
            <span class="tab-label">
              {{ t('admin.oauthClients.tabs.redirectsAndCors', {}, 'Redirects & CORS') }}
              <span
                v-if="redirectIssues.length"
                v-tooltip="{ content: redirectIssues.join(' · '), placement: 'bottom' }"
                class="tab-issue"
                role="img"
                :aria-label="redirectIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
            </span>
          </CoarTab>
          <CoarTab id="lifetimes">{{ t('admin.oauthClients.tabs.tokensAndSessions', {}, 'Tokens & Sessions') }}</CoarTab>
          <CoarTab id="security">{{ t('admin.oauthClients.tabs.security', {}, 'Sicherheit') }}</CoarTab>
          <CoarTab v-if="original?.IsDynamicallyRegistered" id="dcr">
            {{ t('admin.oauthClients.tabs.dcr', {}, 'Registration Info') }}
          </CoarTab>
        </CoarTabGroup>

        <!-- General — stable identity, client profile and operational status. -->
        <div v-show="activeTab === 'general'" class="tab-content tab-content--form">
          <CoarNotice v-if="hasStaffingGrant" truncate variant="info">
            {{ t('admin.oauthClients.terminal.identityProfileNoticeShort', {}, 'Staffing legt Client-Typ und Aktivstatus fest.') }}
            <template #details>
              {{ t('admin.oauthClients.terminal.identityProfileNotice', {}, 'Client ID und Display Name bleiben frei wählbar. Client-Typ und Aktivstatus werden aus Gerätebindung und Terminal-Lifecycle abgeleitet.') }}
            </template>
          </CoarNotice>
          <div class="client-form">
            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.oauthClients.section.identity', {}, 'Identität') }}
                </h3>
              </CoarDivider>
              <div class="modal-form-grid">
                <CoarFormField
                  class="col-half"
                  :label="t('admin.oauthClients.clientId', {}, 'Client ID')"
                  :hint="isCreate
                    ? t('admin.oauthClients.clientIdHint', {}, 'Stabile Protokollkennung; nach dem Erstellen nicht mehr änderbar.')
                    : t('admin.oauthClients.clientIdLockedHint', {}, 'Protokollkennung; nach dem Erstellen unveränderbar.')">
                  <CoarTextInput v-model="form.ClientId" :disabled="!isCreate" :clearable="isCreate" />
                </CoarFormField>
                <CoarFormField
                  class="col-half"
                  :label="t('admin.oauthClients.displayName', {}, 'Display Name')"
                  :hint="t('admin.oauthClients.displayNameHint', {}, 'Lesbarer Name für Administration und Zustimmungsdialoge.')">
                  <CoarTextInput v-model="form.DisplayName" clearable />
                </CoarFormField>
                <CoarFormField
                  class="col-half"
                  :label="t('admin.oauthClients.type', {}, 'Client Type')"
                  :hint="t('admin.oauthClients.typeHint', {}, 'Public Clients können kein Secret sicher verwahren; Confidential Clients schon.')">
                  <CoarSelect v-model="form.ClientType" :options="clientTypeOptions" :disabled="!isCreate || hasStaffingGrant" />
                </CoarFormField>
                <CoarFormField
                  class="col-half client-enabled-field"
                  :label="t('admin.oauthClients.enabled', {}, 'Aktiv')"
                  :hint="t('admin.oauthClients.enabledHint', {}, 'Inaktive Clients können keine Token-Flows starten oder abschließen.')"
                  layout="inline"
                  label-position="after">
                  <CoarCheckbox v-model="form.Enabled" :disabled="hasStaffingGrant" />
                </CoarFormField>
              </div>
            </section>
          </div>
        </div>

        <!-- Login & consent — user-facing policy, separate from client
             authentication and protocol hardening. -->
        <div v-show="activeTab === 'login'" class="tab-content tab-content--form">
          <CoarNotice v-if="hasStaffingGrant" truncate variant="info">
            {{ t('admin.oauthClients.terminal.loginProfileNoticeShort', {}, 'Staffing verwendet implizite Zustimmung.') }}
            <template #details>
              {{ t('admin.oauthClients.terminal.loginProfileNotice', {}, 'Die WebAuthn RP-ID wird beim Erstellen festgelegt, weil sie bestimmt, gegen welchen Host Personal-Passkeys geprüft werden. Eine spätere Änderung erfordert einen neuen Terminal-Slot und erneutes Enrollment.') }}
            </template>
          </CoarNotice>
          <div class="client-form">
            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.oauthClients.section.consent', {}, 'Zustimmungsverhalten') }}
                </h3>
              </CoarDivider>
              <div class="modal-form-grid">
                <CoarFormField
                  class="col-half"
                  :label="t('admin.oauthClients.consentType', {}, 'Consent Type')"
                  :hint="t('admin.oauthClients.consentTypeHint', {}, 'Legt fest, ob und wie Benutzer angeforderte Scopes bestätigen.')">
                  <CoarSelect v-model="form.ConsentType" :options="consentTypeOptions" :disabled="hasStaffingGrant" />
                </CoarFormField>
              </div>
            </section>

            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.oauthClients.section.passkeys', {}, 'Passkeys') }}
                </h3>
              </CoarDivider>
              <div class="modal-form-grid">
                <CoarFormField
                  class="col-full"
                  :label="t('admin.oauthClients.webAuthnRpId', {}, 'WebAuthn RP-ID (Passkeys)')">
                  <CoarTextInput
                    v-model="form.WebAuthnRpId"
                    clearable
                    :disabled="isTerminalManaged"
                    :placeholder="t('admin.oauthClients.webAuthnRpIdPlaceholder', {}, 'leer = Realm-Domain')" />
                  <p class="field-hint">
                    {{ hasStaffingGrant
                      ? t('admin.oauthClients.terminal.rpIdCreateHint', {}, 'Für Staffing erforderlich. Diese RP-ID bindet die Personal-Passkeys an den angegebenen Host.')
                      : t('admin.oauthClients.webAuthnRpIdHint', {}, 'Optional. Eine Änderung macht bereits registrierte Passkeys dieses Clients ungültig.') }}
                  </p>
                </CoarFormField>
              </div>
            </section>
          </div>
        </div>

        <!-- Apps — link the client to one or more registered Applications.
             Empty selection = realm-wide (cross-app, OIDC standard scopes
             only). -->
        <div v-show="activeTab === 'apps'" class="tab-content">
          <CoarNotice v-if="hasStaffingGrant" truncate variant="info">
            {{ t('admin.oauthClients.terminal.appsProfileNoticeShort', {}, 'Apps begrenzen die fachlichen Ressourcen dieses Staffing-Clients.') }}
            <template #details>
              {{ t('admin.oauthClients.terminal.appsProfileNotice', {}, 'Wähle die Apps, zu denen die erlaubten Business-Scopes und Ziel-APIs gehören. Position und Besetzung bleiben davon getrennt.') }}
            </template>
          </CoarNotice>
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <div class="section-divider__label">
              <CoarPopover class="inline-info-popover" mode="both" :offset="8">
                  <button
                    type="button"
                    class="selection-help"
                    :aria-label="t('admin.oauthClients.apps.helpAria', {}, 'Details zur App-Zuordnung')">
                    <CoarIcon name="info" size="s" aria-hidden="true" />
                  </button>
                  <template #content>
                    <div class="selection-popover">
                      <h4>{{ t('admin.oauthClients.apps.helpTitle', {}, 'App-Zuordnung') }}</h4>

                      <section>
                        <h5>{{ t('admin.oauthClients.apps.helpScopeTitle', {}, 'Gültigkeitsbereich') }}</h5>
                        <ul>
                          <li>
                            {{ t('admin.oauthClients.apps.helpEmpty', {}, 'Keine App verknüpft: Der Client gilt realm-weit und kann nur Standard-OIDC-Scopes verwenden.') }}
                          </li>
                          <li>
                            {{ t('admin.oauthClients.apps.helpMultiple', {}, 'Mehrere Apps verknüpft: Der Client kann app-übergreifend agieren.') }}
                          </li>
                        </ul>
                      </section>

                      <section>
                        <h5>{{ t('admin.oauthClients.apps.helpSelectionTitle', {}, 'Mehrfachauswahl') }}</h5>
                        <ul>
                          <li>{{ t('admin.oauthClients.apps.helpMulti', {}, 'Strg/Cmd + Klick wählt einzelne Einträge.') }}</li>
                          <li>{{ t('admin.oauthClients.apps.helpRange', {}, 'Shift + Klick wählt einen Bereich.') }}</li>
                          <li>{{ t('admin.oauthClients.apps.helpDrag', {}, 'Einträge können auch zwischen den Spalten gezogen werden.') }}</li>
                        </ul>
                      </section>
                    </div>
                  </template>
              </CoarPopover>
              <h3>{{ t('admin.oauthClients.apps.assignment', {}, 'App-Zuordnung') }}</h3>
            </div>
          </CoarDivider>
          <section class="flex-section">
            <CoarDualListbox
              class="flex-1 min-h-0"
              v-model="form.AppIds"
              :options="appOptions"
              drag-drop
              sort-options="asc"
              :search-fields="['label', 'subtitle', 'group']"
              :available-label="t('admin.oauthClients.apps.available', {}, 'Available apps')"
              :selected-label="t('admin.oauthClients.apps.selected', {}, 'Linked')"
              :search-placeholder="t('admin.oauthClients.apps.searchPlaceholder', {}, 'Search apps…')" />
          </section>
        </div>

        <!-- Scopes — explicitly opt-in. OpenIddict rejects /connect/authorize
             and /connect/token requests for any scope not on this list. -->
        <div v-show="activeTab === 'scopes'" class="tab-content">
          <CoarNotice v-if="hasStaffingGrant" truncate variant="info">
            {{ t('admin.oauthClients.terminal.scopesProfileNoticeShort', {}, 'Scope-Ressourcen werden zu Audiences des Staffing-Tokens.') }}
            <template #details>
              {{ t('admin.oauthClients.terminal.scopesProfileNotice', {}, 'Der Terminal-Client darf nur ausgewählte Business-Scopes anfordern. Deren API-Ressourcen erzeugen aud; Position-Rollen und -Berechtigungen erzeugen resource_access.') }}
            </template>
          </CoarNotice>
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <div class="section-divider__label">
              <CoarPopover class="inline-info-popover" mode="both" :offset="8">
                <button
                  type="button"
                  class="selection-help"
                  :aria-label="t('admin.oauthClients.scopes.helpAria', {}, 'Details zur Scope-Auswahl')">
                  <CoarIcon name="info" size="s" aria-hidden="true" />
                </button>
                <template #content>
                  <div class="selection-popover">
                    <h4>{{ t('admin.oauthClients.scopes.helpTitle', {}, 'Scope-Auswahl') }}</h4>

                    <section>
                      <h5>{{ t('admin.oauthClients.scopes.helpModgudTitle', {}, 'App-Zuordnung') }}</h5>
                      <p>{{ t('admin.oauthClients.scopes.helpApp', {}, 'App-spezifische Scopes können nur angefordert werden, wenn der Client mit der zugehörigen App verknüpft ist.') }}</p>
                    </section>

                    <section>
                      <h5>{{ t('admin.oauthClients.scopes.helpSelectionTitle', {}, 'Mehrfachauswahl') }}</h5>
                      <ul>
                        <li>{{ t('admin.oauthClients.scopes.helpMulti', {}, 'Strg/Cmd + Klick wählt einzelne Einträge.') }}</li>
                        <li>{{ t('admin.oauthClients.scopes.helpRange', {}, 'Shift + Klick wählt einen Bereich.') }}</li>
                        <li>{{ t('admin.oauthClients.scopes.helpDrag', {}, 'Einträge können auch zwischen den Spalten gezogen werden.') }}</li>
                      </ul>
                    </section>
                  </div>
                </template>
              </CoarPopover>
              <h3>{{ t('admin.oauthClients.scopes.assignment', {}, 'Scope-Auswahl') }}</h3>
            </div>
          </CoarDivider>
          <section class="flex-section">
            <CoarDualListbox
              class="flex-1 min-h-0"
              v-model="form.Scopes"
              :options="scopeOptions"
              drag-drop
              sort-options="asc"
              :search-fields="['label', 'subtitle', 'group']"
              :available-label="t('admin.oauthClients.scopes.available', {}, 'Available scopes')"
              :selected-label="t('admin.oauthClients.scopes.selected', {}, 'Allowed')"
              :search-placeholder="t('admin.oauthClients.scopes.searchPlaceholder', {}, 'Search scopes…')" />
          </section>
        </div>

        <!-- Flows — grant selection and its dependent M2M owner live together.
             Empty selection produces a client that cannot mint tokens. -->
        <div v-show="activeTab === 'grants'" class="tab-content">
            <CoarNotice v-if="flowIssues.length" truncate variant="warning" class="flow-issues-notice">
              {{ t('admin.oauthClients.validation.incompleteShort', { count: flowIssues.length }, `Konfiguration unvollständig (${flowIssues.length})`) }}
              <template #details>
                <ul>
                <li v-for="issue in flowIssues" :key="issue">{{ issue }}</li>
                </ul>
              </template>
            </CoarNotice>

            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <div class="section-divider__label">
                <CoarPopover class="inline-info-popover" mode="both" :offset="8">
                <button
                  type="button"
                  class="selection-help"
                  :aria-label="t('admin.oauthClients.grantTypes.helpAria', {}, 'Details zur Flow-Auswahl')">
                  <CoarIcon name="info" size="s" aria-hidden="true" />
                </button>
                <template #content>
                  <div class="selection-popover">
                    <h4>{{ t('admin.oauthClients.grantTypes.helpTitle', {}, 'Flow-Auswahl') }}</h4>

                    <section>
                      <h5>{{ t('admin.oauthClients.grantTypes.helpPrincipleTitle', {}, 'Grundsatz') }}</h5>
                      <p>
                        {{ t('admin.oauthClients.grantTypes.helpPrinciple', {}, 'Aktiviere nur die Flows, die der Client tatsächlich benötigt. Es gibt keine stillen Defaults.') }}
                      </p>
                    </section>

                    <section>
                      <h5>{{ t('admin.oauthClients.grantTypes.helpCombinationsTitle', {}, 'Typische Kombinationen') }}</h5>
                      <ul>
                        <li><strong>{{ t('admin.oauthClients.grantTypes.helpSpa', {}, 'SPA / Mobile') }}:</strong> authorization_code + refresh_token</li>
                        <li><strong>{{ t('admin.oauthClients.grantTypes.helpMachine', {}, 'Server-zu-Server') }}:</strong> client_credentials + Service Account</li>
                        <li><strong>{{ t('admin.oauthClients.grantTypes.helpDevice', {}, 'TV / CLI / Gerät ohne Browser') }}:</strong> device_code + refresh_token</li>
                        <li><strong>{{ t('admin.oauthClients.grantTypes.helpTerminal', {}, 'Geteiltes Arbeitsplatz-Terminal') }}:</strong> {{ t('admin.oauthClients.grantTypes.helpTerminalProfile', {}, 'staffing + Position; technische Grants werden automatisch ergänzt') }}</li>
                      </ul>
                    </section>

                    <section>
                      <h5>{{ t('admin.oauthClients.grantTypes.helpSelectionTitle', {}, 'Mehrfachauswahl') }}</h5>
                      <ul>
                        <li>{{ t('admin.oauthClients.grantTypes.helpMulti', {}, 'Strg/Cmd + Klick wählt einzelne Einträge.') }}</li>
                        <li>{{ t('admin.oauthClients.grantTypes.helpRange', {}, 'Shift + Klick wählt einen Bereich.') }}</li>
                        <li>{{ t('admin.oauthClients.grantTypes.helpDrag', {}, 'Einträge können auch zwischen den Spalten gezogen werden.') }}</li>
                      </ul>
                    </section>
                  </div>
                </template>
                </CoarPopover>
                <h3>{{ t('admin.oauthClients.grantTypes.assignment', {}, 'Flow-Auswahl') }}</h3>
              </div>
            </CoarDivider>

            <CoarNotice truncate v-if="nativeGrantsEnabled" variant="info">
              {{ t('admin.oauthClients.grantTypes.nativeHintShort', {}, 'Passwordless grants are enabled for this realm — add one to allow it for this client.') }}
              <template #details>
                {{ t('admin.oauthClients.grantTypes.nativeHint', {}, 'Native passwordless grants (urn:cocoar:otp / :magic / :passkey) are enabled for this realm and available below. Add one here to give this client the matching gt:urn:cocoar:* permission — only then can it exchange a passwordless proof at /connect/token.') }}
              </template>
            </CoarNotice>
            <CoarNotice truncate v-if="hasNativeGrantWithRealmOff" variant="warning">
              {{ t('admin.oauthClients.grantTypes.nativeDisabledWarningShort', {}, 'A native grant is selected but disabled for this realm — it will not work.') }}
              <template #details>
                {{ t('admin.oauthClients.grantTypes.nativeDisabledWarning', {}, 'This client has a native passwordless grant (urn:cocoar:otp / :magic / :passkey) selected, but native grants are DISABLED for this realm — so it will not work: the token endpoint rejects the grant and the OTP-request endpoint returns an error instead of emailing a code. Enable them under Realm Settings → Native Passwordless Grants.') }}
              </template>
            </CoarNotice>
            <section
              class="flex-section"
              :class="{
                'flex-section--with-service-account': form.AllowedGrantTypes.includes('client_credentials') || form.LinkedServiceAccountId,
              }">
              <CoarDualListbox
                class="flex-1 min-h-0"
                v-model="form.AllowedGrantTypes"
                :options="grantTypeOptions"
                drag-drop
                sort-options="asc"
                :search-fields="['label', 'subtitle', 'group']"
                :available-label="t('admin.oauthClients.grantTypes.available', {}, 'Available grant types')"
                :selected-label="t('admin.oauthClients.grantTypes.selected', {}, 'Enabled')"
                :search-placeholder="t('admin.oauthClients.grantTypes.searchPlaceholder', {}, 'Search…')" />
            </section>
            <div
              v-if="form.AllowedGrantTypes.includes('client_credentials') || form.LinkedServiceAccountId"
              class="flow-service-account">
              <div class="service-account-picker">
                <CoarFormField
                  class="service-account-picker__select"
                  :label="t('admin.oauthClients.serviceAccount', {}, 'Zugehöriger Service Account')"
                  :hint="t('admin.oauthClients.serviceAccountHint', {}, 'Pflicht für reine client_credentials-Clients; die Zuordnung ist nach dem Erstellen unveränderbar.')">
                  <CoarSelect
                    v-if="!useNewServiceAccountDraft"
                    v-model="form.LinkedServiceAccountId"
                    :options="serviceAccountOptions"
                    :disabled="!isCreate" />
                  <div v-else class="service-account-draft">
                    <div class="service-account-draft__identity">
                      <CoarIcon name="cpu" size="m" />
                      <div class="service-account-draft__text">
                        <div class="flex items-center gap-2">
                          <strong>{{ newServiceAccountForm.AccountName }}</strong>
                          <CoarTag :variant="newServiceAccountForm.IsActive ? 'success' : 'warning'">
                            {{ newServiceAccountForm.IsActive
                              ? t('common.active', {}, 'Aktiv')
                              : t('common.inactive', {}, 'Inaktiv') }}
                          </CoarTag>
                        </div>
                        <span>
                          {{ newServiceAccountForm.Purpose || t('admin.oauthClients.newServiceAccount.noPurpose', {}, 'Kein Verwendungszweck angegeben') }}
                        </span>
                      </div>
                    </div>
                    <div class="service-account-draft__actions">
                      <CoarButton size="s" variant="tertiary" @click="openNewServiceAccountModal">
                        {{ t('common.edit', {}, 'Bearbeiten') }}
                      </CoarButton>
                      <CoarButton size="s" variant="tertiary" @click="discardNewServiceAccountDraft">
                        {{ t('admin.oauthClients.newServiceAccount.discard', {}, 'Verwerfen') }}
                      </CoarButton>
                    </div>
                  </div>
                </CoarFormField>
                <CoarButton
                  v-if="isCreate && !useNewServiceAccountDraft"
                  size="s"
                  variant="secondary"
                  icon-start="plus"
                  class="service-account-picker__create"
                  @click="openNewServiceAccountModal">
                  {{ t('admin.oauthClients.newServiceAccount.button', {}, 'Neu anlegen') }}
                </CoarButton>
              </div>
            </div>
        </div>

        <!-- Terminal — appears only for the staffing grant. Keeping slot and
             position metadata out of Flows preserves the compact grant picker
             while giving terminal-specific validation its own destination. -->
        <div v-show="activeTab === 'terminal' && hasStaffingGrant" class="tab-content tab-content--form">
          <CoarNotice v-if="terminalIssues.length" truncate variant="warning" class="flow-issues-notice">
            {{ t('admin.oauthClients.validation.incompleteShort', { count: terminalIssues.length }, `Konfiguration unvollständig (${terminalIssues.length})`) }}
            <template #details>
              <ul>
                <li v-for="issue in terminalIssues" :key="issue">{{ issue }}</li>
              </ul>
            </template>
          </CoarNotice>

          <div class="client-form flow-terminal">
            <div class="service-account-picker">
              <CoarFormField
                class="service-account-picker__select"
                :label="t('admin.oauthClients.position', {}, 'Zugehörige Position')"
                :hint="t('admin.oauthClients.positionHint', {}, 'Wähle genau eine terminalfähige Position oder lege sie zusammen mit diesem Client neu an.')">
                <CoarSelect
                  v-if="!newPositionDraft"
                  v-model="form.LinkedPositionPrincipalId"
                  :options="positionOptions"
                  :disabled="!isCreate" />
                <div v-else class="service-account-draft">
                  <div class="service-account-draft__identity">
                    <CoarIcon name="briefcase" size="m" />
                    <div class="service-account-draft__text">
                      <div class="flex items-center gap-2">
                        <strong>{{ newPositionDraft.AccountName }}</strong>
                        <CoarTag :variant="newPositionDraft.IsActive === false ? 'warning' : 'success'">
                          {{ newPositionDraft.IsActive === false
                            ? t('common.inactive', {}, 'Inaktiv')
                            : t('common.active', {}, 'Aktiv') }}
                        </CoarTag>
                      </div>
                      <span>{{ newPositionDraft.Purpose || t('admin.oauthClients.newPosition.noPurpose', {}, 'Kein Verwendungszweck angegeben') }}</span>
                    </div>
                  </div>
                  <div class="service-account-draft__actions">
                    <CoarButton size="s" variant="tertiary" @click="openNewPositionModal">
                      {{ t('common.edit', {}, 'Bearbeiten') }}
                    </CoarButton>
                    <CoarButton size="s" variant="tertiary" @click="discardNewPositionDraft">
                      {{ t('admin.oauthClients.newPosition.discard', {}, 'Verwerfen') }}
                    </CoarButton>
                  </div>
                </div>
              </CoarFormField>
              <CoarButton
                v-if="isCreate && !newPositionDraft"
                size="s"
                variant="secondary"
                icon-start="plus"
                class="service-account-picker__create"
                @click="openNewPositionModal">
                {{ t('admin.oauthClients.newPosition.button', {}, 'Neu anlegen') }}
              </CoarButton>
            </div>

            <div class="modal-form-grid terminal-fields">
              <CoarFormField
                class="col-half"
                required
                :label="t('admin.oauthClients.terminal.name', {}, 'Terminalname')"
                :hint="t('admin.oauthClients.terminal.nameHint', {}, 'Benennung des physischen Slots, z. B. „Tor links“.')">
                <CoarTextInput v-model="form.TerminalDisplayName" clearable placeholder="Tor links" :disabled="!isCreate" />
              </CoarFormField>
              <CoarFormField
                class="col-half"
                :label="t('admin.oauthClients.terminal.location', {}, 'Standort')"
                :hint="t('admin.oauthClients.terminal.locationHint', {}, 'Optionale Ortsangabe für Administration und Audit.')">
                <CoarTextInput v-model="form.TerminalLocation" clearable placeholder="Tor 3" :disabled="!isCreate" />
              </CoarFormField>
              <CoarFormField
                class="col-half"
                required
                :label="t('admin.oauthClients.terminal.rpId', {}, 'WebAuthn RP-ID')"
                :hint="t('admin.oauthClients.terminal.rpIdHint', {}, 'Hostname, gegen den die Passkeys des Personals geprüft werden.')">
                <CoarTextInput v-model="form.WebAuthnRpId" clearable placeholder="alerthub.example.com" :disabled="!isCreate" />
              </CoarFormField>
              <CoarFormField
                class="col-half"
                required
                :label="t('admin.oauthClients.terminal.binding', {}, 'Gerätebindung')"
                :hint="t('admin.oauthClients.terminal.bindingHint', {}, 'Legt das unveränderbare Authentifizierungsprofil dieses Terminal-Slots fest.')">
                <CoarSelect v-model="form.TerminalBinding" :options="terminalBindingOptions" :disabled="!isCreate" />
              </CoarFormField>
            </div>

            <CoarNotice v-if="isCreate" truncate variant="info">
              {{ t('admin.oauthClients.terminal.atomicHintShort', {}, 'Position, Terminal-Slot und OAuth-Client werden gemeinsam erstellt.') }}
              <template #details>
                {{ t('admin.oauthClients.terminal.atomicHint', {}, 'Die Anlage erfolgt atomar: Schlägt ein Teil fehl, wird nichts gespeichert.') }}
              </template>
            </CoarNotice>
            <CoarNotice v-if="!selectedPositionAllowsBinding || !newPositionAllowsBinding" truncate variant="warning">
              {{ t('admin.oauthClients.terminal.bindingMismatchShort', {}, 'Die Gerätebindung passt nicht zur Position.') }}
              <template #details>
                {{ t('admin.oauthClients.terminal.bindingMismatch', {}, 'Die gewählte Position erlaubt diese Gerätebindung nicht. Wähle eine andere Bindung oder passe die Position an.') }}
              </template>
            </CoarNotice>
            <CoarNotice v-if="form.TerminalBinding === 'none'" truncate variant="warning">
              {{ t('admin.oauthClients.terminal.noneWarningShort', {}, 'Keine Gerätebindung ausgewählt.') }}
              <template #details>
                {{ t('admin.oauthClients.terminal.noneWarning', {}, 'Ohne Gerätebindung besitzt das Terminal keine nachweisbare Geräteidentität; die Admin-Freigabe ist dann die einzige Ausgabebarriere.') }}
              </template>
            </CoarNotice>
          </div>
        </div>

        <!-- Redirects & CORS — browser-facing endpoint allow-lists only. -->
        <div v-show="activeTab === 'urls'" class="tab-content tab-content--form">
          <CoarNotice v-if="hasStaffingGrant" truncate variant="info">
            {{ t('admin.oauthClients.terminal.urlsProfileNoticeShort', {}, 'Staffing benötigt keine Redirect- oder CORS-Adressen.') }}
            <template #details>
              {{ t('admin.oauthClients.terminal.urlsProfileNotice', {}, 'Staffing nutzt den Device Flow. Redirect-, Post-Logout- und CORS-Adressen sind deshalb für dieses Clientprofil nicht konfigurierbar.') }}
            </template>
          </CoarNotice>
          <div class="client-form url-configurator">
            <nav
              class="url-list-nav"
              :aria-label="t('admin.oauthClients.urls.navigation', {}, 'Redirect and CORS lists')">
              <button
                type="button"
                class="url-list-nav__item"
                :class="{ 'url-list-nav__item--active': activeUrlList === 'redirect' }"
                :aria-current="activeUrlList === 'redirect' ? 'page' : undefined"
                @click="activeUrlList = 'redirect'">
                <span>{{ t('admin.oauthClients.redirectUris', {}, 'Redirect-URIs') }}</span>
                <span class="url-list-nav__count">{{ form.RedirectUris.length }}</span>
              </button>
              <button
                type="button"
                class="url-list-nav__item"
                :class="{ 'url-list-nav__item--active': activeUrlList === 'post-logout' }"
                :aria-current="activeUrlList === 'post-logout' ? 'page' : undefined"
                @click="activeUrlList = 'post-logout'">
                <span>{{ t('admin.oauthClients.postLogoutRedirectUris', {}, 'Post-Logout Redirect-URIs') }}</span>
                <span class="url-list-nav__count">{{ form.PostLogoutRedirectUris.length }}</span>
              </button>
              <button
                type="button"
                class="url-list-nav__item"
                :class="{ 'url-list-nav__item--active': activeUrlList === 'cors' }"
                :aria-current="activeUrlList === 'cors' ? 'page' : undefined"
                @click="activeUrlList = 'cors'">
                <span>{{ t('admin.oauthClients.corsOrigins', {}, 'CORS-Origins') }}</span>
                <span class="url-list-nav__count">{{ form.AllowedCorsOrigins.length }}</span>
              </button>
            </nav>

            <div class="url-configurator__panel">
              <EditableStringList
                v-show="activeUrlList === 'redirect'"
                v-model="form.RedirectUris"
                :disabled="hasStaffingGrant"
                appearance="panel-grid"
                min-height="100%"
                :search-placeholder="t('common.search', {}, 'Search…')"
                :placeholder="t('admin.oauthClients.redirectUri.placeholder', {}, 'https://app.example.com/signin-oidc')" />
              <EditableStringList
                v-show="activeUrlList === 'post-logout'"
                v-model="form.PostLogoutRedirectUris"
                :disabled="hasStaffingGrant"
                appearance="panel-grid"
                min-height="100%"
                :search-placeholder="t('common.search', {}, 'Search…')"
                :placeholder="t('admin.oauthClients.postLogoutRedirectUri.placeholder', {}, 'https://app.example.com/signout-callback-oidc')" />
              <EditableStringList
                v-show="activeUrlList === 'cors'"
                v-model="form.AllowedCorsOrigins"
                :disabled="hasStaffingGrant"
                appearance="panel-grid"
                min-height="100%"
                :search-placeholder="t('common.search', {}, 'Search…')"
                :placeholder="t('admin.oauthClients.corsOrigin.placeholder', {}, 'https://app.example.com')" />
            </div>
          </div>
        </div>

        <!-- Security — credentials and protocol hardening stay fully editable
             before the first save. -->
        <div v-show="activeTab === 'security'" class="tab-content tab-content--form">
          <CoarNotice v-if="hasStaffingGrant" truncate variant="info">
            {{ t('admin.oauthClients.terminal.securityProfileNoticeShort', {}, 'Die Gerätebindung legt Secret und DPoP fest.') }}
            <template #details>
              {{ t('admin.oauthClients.terminal.securityProfileNotice', {}, 'Secret- und DPoP-Einstellungen werden aus der Gerätebindung abgeleitet. Das Client-Secret wird bei Bedarf sicher vom Server erzeugt.') }}
            </template>
          </CoarNotice>
          <div class="client-form security-form">
            <section class="security-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.oauthClients.section.authentication', {}, 'Client-Authentifizierung') }}
                </h3>
              </CoarDivider>
              <div class="modal-form-grid">
                <CoarFormField
                  v-if="isCreate && form.ClientType === 'confidential'"
                  class="col-half"
                  :label="t('admin.oauthClients.clientSecret', {}, 'Client Secret (empty = generate)')"
                  :hint="t('admin.oauthClients.clientSecretHint', {}, 'Leer lassen, um beim Erstellen ein starkes einmalig sichtbares Secret zu erzeugen.')">
                  <CoarPasswordInput v-model="form.ClientSecret" clearable :disabled="hasStaffingGrant" />
                </CoarFormField>
                <div
                  v-else-if="isExistingClient && form.ClientType === 'confidential' && !form.LinkedServiceAccountId"
                  class="col-half credential-action">
                  <span class="field-label">{{ t('admin.oauthClients.clientSecret', {}, 'Client Secret') }}</span>
                  <CoarButton size="s" variant="secondary" icon-start="rotate-ccw" :loading="loading" @click="regenerateSecret">
                    {{ t('admin.oauthClients.regenerate', {}, 'Regenerate Client Secret') }}
                  </CoarButton>
                  <p class="field-hint">
                    {{ t('admin.oauthClients.regenerateHint', {}, 'Das bisherige Secret wird sofort ungültig.') }}
                  </p>
                </div>
                <CoarNotice v-if="form.ClientType === 'public'" variant="info" class="col-full">
                  {{ t('admin.oauthClients.publicClientSecretHint', {}, 'Public Clients verwenden kein Client Secret. Für Authorization Code ist PKCE erforderlich.') }}
                </CoarNotice>
                <CoarNotice v-else-if="form.LinkedServiceAccountId && !isCreate" variant="info" class="col-full">
                  {{ t('admin.oauthClients.serviceAccountSecretHint', {}, 'Dieses Secret wird über den zugehörigen Service Account verwaltet und dort rotiert.') }}
                </CoarNotice>
              </div>
            </section>

            <section class="security-section">
              <CoarDivider
                align="left"
                variant="subtle"
                :width="100"
                :spacing-top="12"
                :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.oauthClients.section.protocolSecurity', {}, 'Protokollabsicherung') }}
                </h3>
              </CoarDivider>
              <div class="security-policy-grid">
                <div class="security-policy-card">
                  <div class="security-policy-card__heading">
                    <CoarPopover class="inline-info-popover" mode="both" :offset="8">
                      <button
                        type="button"
                        class="selection-help"
                        :aria-label="t('admin.oauthClients.protocolParHelpAria', {}, 'Details zu PAR')">
                        <CoarIcon name="info" size="s" aria-hidden="true" />
                      </button>
                      <template #content>
                        <div class="selection-popover selection-popover--compact">
                          <h4>{{ t('admin.oauthClients.protocolParTitle', {}, 'Pushed Authorization Requests (PAR)') }}</h4>
                          <p>{{ t('admin.oauthClients.requireParCardHint', {}, 'Direkte Authorize-Aufrufe ablehnen; Parameter müssen zuerst über /connect/par übertragen werden.') }}</p>
                        </div>
                      </template>
                    </CoarPopover>
                    <h4>{{ t('admin.oauthClients.protocolParTitle', {}, 'Pushed Authorization Requests (PAR)') }}</h4>
                  </div>
                  <CoarCheckbox
                    v-model="form.RequirePushedAuthorizationRequests"
                    :disabled="hasStaffingGrant"
                    :label="t('admin.oauthClients.requirePar', {}, 'Require Pushed Authorization Requests (PAR)')" />
                </div>

                <div class="security-policy-card">
                  <div class="security-policy-card__heading">
                    <CoarPopover class="inline-info-popover" mode="both" :offset="8">
                      <button
                        type="button"
                        class="selection-help"
                        :aria-label="t('admin.oauthClients.protocolDpopHelpAria', {}, 'Details zu DPoP')">
                        <CoarIcon name="info" size="s" aria-hidden="true" />
                      </button>
                      <template #content>
                        <div class="selection-popover">
                          <h4>DPoP</h4>
                          <section>
                            <h5>{{ t('admin.oauthClients.requireDpop', {}, 'Require DPoP') }}</h5>
                            <p>{{ t('admin.oauthClients.requireDpopCardHint', {}, 'Access-Tokens an den Proof-Key binden und Anfragen ohne DPoP-Proof ablehnen.') }}</p>
                          </section>
                          <section>
                            <h5>{{ t('admin.oauthClients.requireDpopNonce', {}, 'Require DPoP nonce') }}</h5>
                            <p>{{ t('admin.oauthClients.requireDpopNonceCardHint', {}, 'Im DPoP-Proof eine vom Server ausgestellte Nonce verlangen.') }}</p>
                          </section>
                        </div>
                      </template>
                    </CoarPopover>
                    <h4>DPoP</h4>
                  </div>
                  <div class="security-policy-card__toggles">
                    <CoarCheckbox
                      v-model="form.RequireDpop"
                      :disabled="hasStaffingGrant"
                      :label="t('admin.oauthClients.requireDpop', {}, 'Require DPoP')" />
                    <CoarCheckbox
                      v-model="form.RequireDpopNonce"
                      :disabled="hasStaffingGrant"
                      :label="t('admin.oauthClients.requireDpopNonce', {}, 'Require DPoP nonce')" />
                  </div>
                </div>
              </div>
            </section>

          </div>
        </div>

        <!-- Tokens & Sessions — token representation and all time-based policy. -->
        <div v-show="activeTab === 'lifetimes'" class="tab-content tab-content--form">
          <CoarNotice v-if="hasStaffingGrant" truncate variant="info">
            {{ t('admin.oauthClients.terminal.lifetimesProfileNoticeShort', {}, 'Staffing verwendet Reference-Tokens und geerbte Laufzeiten.') }}
            <template #details>
              {{ t('admin.oauthClients.terminal.lifetimesProfileNotice', {}, 'Token- und Session-Laufzeiten werden aus den übergeordneten App-/Realm- und Positionsrichtlinien übernommen.') }}
            </template>
          </CoarNotice>
          <div class="client-form lifetime-form">
            <section class="lifetime-section">
              <CoarDivider
                align="left"
                variant="subtle"
                :width="100"
                :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.oauthClients.section.tokenFormat', {}, 'Token-Format') }}
                </h3>
              </CoarDivider>
              <CoarFormField
                :label="t('admin.oauthClients.accessTokenType', {}, 'Access-Token-Typ')"
                :hint="t('admin.oauthClients.accessTokenType.hint', {}, 'Reference-Tokens werden per Introspection aufgelöst; JWTs werden lokal anhand der Signatur validiert.')">
                <CoarSelect v-model="form.AccessTokenType" :options="accessTokenTypeOptions" :disabled="hasStaffingGrant" class="input-enum" />
              </CoarFormField>
            </section>

            <section class="lifetime-section">
              <CoarDivider
                align="left"
                variant="subtle"
                :width="100"
                :spacing-top="12"
                :spacing-bottom="12">
                <div class="section-divider__label">
                  <CoarPopover class="inline-info-popover" mode="both" :offset="8">
                    <button
                      type="button"
                      class="selection-help"
                      :aria-label="t('admin.oauthClients.lifetimesHelpAria', {}, 'Details zu Token-Laufzeiten')">
                      <CoarIcon name="info" size="s" aria-hidden="true" />
                    </button>
                    <template #content>
                      <div class="selection-popover selection-popover--compact">
                        <h4>{{ t('admin.oauthClients.section.tokenLifetimes', {}, 'Token-Laufzeiten') }}</h4>
                        <p>{{ t('admin.oauthClients.lifetimesHint', {}, 'Werte in Sekunden. Leer = Default des IdP.') }}</p>
                      </div>
                    </template>
                  </CoarPopover>
                  <h3>{{ t('admin.oauthClients.section.tokenLifetimes', {}, 'Token-Laufzeiten') }}</h3>
                </div>
                </CoarDivider>
              <div class="lifetime-grid lifetime-grid--tokens">
                <CoarFormField class="lifetime-field" :label="t('admin.oauthClients.identityTokenLifetime', {}, 'Identity-Token')">
                  <CoarNumberInput
                    v-model="form.IdentityTokenLifetime"
                    v-bind="lifetimeInputBounds.shortToken"
                    :disabled="hasStaffingGrant"
                    stepper-buttons="both"
                    clearable
                    class="input-number" />
                </CoarFormField>
                <CoarFormField class="lifetime-field" :label="t('admin.oauthClients.accessTokenLifetime', {}, 'Access-Token')">
                  <CoarNumberInput
                    v-model="form.AccessTokenLifetime"
                    v-bind="lifetimeInputBounds.shortToken"
                    :disabled="hasStaffingGrant"
                    stepper-buttons="both"
                    clearable
                    class="input-number" />
                </CoarFormField>
                <CoarFormField class="lifetime-field" :label="t('admin.oauthClients.authCodeLifetime', {}, 'Authorization-Code')">
                  <CoarNumberInput
                    v-model="form.AuthorizationCodeLifetime"
                    v-bind="lifetimeInputBounds.authorizationCode"
                    :disabled="hasStaffingGrant"
                    stepper-buttons="both"
                    clearable
                    class="input-number" />
                </CoarFormField>
                <CoarFormField class="lifetime-field" :label="t('admin.oauthClients.slidingRefreshLifetime', {}, 'Sliding Refresh-Token')">
                  <CoarNumberInput
                    v-model="form.SlidingRefreshTokenLifetime"
                    v-bind="lifetimeInputBounds.refreshToken"
                    :disabled="hasStaffingGrant"
                    stepper-buttons="both"
                    clearable
                    class="input-number" />
                </CoarFormField>
              </div>
            </section>

            <section class="lifetime-section">
              <CoarDivider
                align="left"
                variant="subtle"
                :width="100"
                :spacing-top="12"
                :spacing-bottom="12">
                <div class="section-divider__label">
                  <CoarPopover class="inline-info-popover" mode="both" :offset="8">
                    <button
                      type="button"
                      class="selection-help"
                      :aria-label="t('admin.oauthClients.clientSessionsHelpAria', {}, 'Details zu Client-Sessions')">
                      <CoarIcon name="info" size="s" aria-hidden="true" />
                    </button>
                    <template #content>
                      <div class="selection-popover selection-popover--compact">
                        <h4>{{ t('admin.oauthClients.section.clientSessions', {}, 'Client-Sessions') }}</h4>
                        <p>{{ t('admin.oauthClients.clientSessionsHintShort', {}, 'Idle-Lebensdauer gleitet bei Nutzung, die absolute Lebensdauer bleibt fix.') }}</p>
                        <p>{{ t('admin.oauthClients.clientSessionsHint', {}, 'Client-Sessions begrenzen die Nutzung von Refresh-Tokens. Leere Werte erben die Richtlinie der verknüpften App und danach des Realms.') }}</p>
                      </div>
                    </template>
                  </CoarPopover>
                  <h3>{{ t('admin.oauthClients.section.clientSessions', {}, 'Client-Sessions') }}</h3>
                </div>
              </CoarDivider>
              <div class="lifetime-grid lifetime-grid--sessions">
                <CoarFormField class="lifetime-field" :label="t('admin.oauthClients.clientSessionIdleLifetime', {}, 'Idle-Lebensdauer')">
                  <CoarNumberInput
                    v-model="form.ClientSessionIdleLifetime"
                    v-bind="lifetimeInputBounds.clientSession"
                    :disabled="hasStaffingGrant"
                    stepper-buttons="both"
                    clearable
                    class="input-number" />
                </CoarFormField>
                <CoarFormField class="lifetime-field" :label="t('admin.oauthClients.clientSessionAbsoluteLifetime', {}, 'Absolute Lebensdauer')">
                  <CoarNumberInput
                    v-model="form.ClientSessionAbsoluteLifetime"
                    v-bind="lifetimeInputBounds.clientSession"
                    :disabled="hasStaffingGrant"
                    stepper-buttons="both"
                    clearable
                    class="input-number" />
                </CoarFormField>
              </div>
            </section>
          </div>
        </div>

        <!-- Registration Info — DCR clients only -->
        <div v-show="activeTab === 'dcr' && original?.IsDynamicallyRegistered" class="tab-content tab-content--form">
          <div class="client-form">
            <p class="tab-hint">
              {{ t('admin.oauthClients.dcrInfoHint', {}, 'This client was registered anonymously via POST /connect/register. The fields below are the audit trail captured at registration time and on each successful token issue.') }}
            </p>
            <div class="lifetime-grid">
              <CoarFormField :label="t('admin.oauthClients.dcr.registeredAt', {}, 'Registered at (UTC)')">
                <CoarTextInput :model-value="original?.DcrRegisteredAt ?? ''" disabled class="input-name" />
              </CoarFormField>
              <CoarFormField :label="t('admin.oauthClients.dcr.registeredFromIp', {}, 'Registered from IP')">
                <CoarTextInput :model-value="original?.DcrRegisteredFromIp ?? ''" disabled class="input-name" />
              </CoarFormField>
              <CoarFormField :label="t('admin.oauthClients.dcr.lastUsedAt', {}, 'Last successful token-issue')">
                <CoarTextInput :model-value="original?.DcrLastUsedAt ?? ''" disabled class="input-name" />
              </CoarFormField>
            </div>
          </div>
        </div>
      </section>
    </div>
  </ModalLayout>
</template>

<style scoped>
/* Body-level layout — flex column so children (banner + content) stack. */
.modal-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
  gap: 12px;
}


.secret-banner {
  flex-shrink: 0;
}

.client-editor {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
}

.tab-bar {
  flex-shrink: 0;
  margin-bottom: 12px;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.tab-issue {
  display: inline-flex;
  color: var(--coar-text-semantic-warning, #a15c00);
  cursor: help;
}

.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding-bottom: 16px;
  min-height: 0;
  overflow-y: auto;
}
.tab-content--form {
  align-items: flex-start;
}

.client-form {
  width: 100%;
  max-width: 64rem;
  min-width: 0;
}

.url-configurator {
  display: grid;
  flex: 1;
  grid-template-columns: minmax(12rem, 14rem) minmax(0, 1fr);
  gap: 1rem;
  align-items: stretch;
  width: 100%;
  max-width: none;
  min-height: 0;
  overflow: hidden;
}

.url-list-nav {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  padding-right: 1rem;
  border-right: 1px solid var(--coar-border-neutral, #e5e7eb);
}

.url-list-nav__item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  justify-content: space-between;
  width: 100%;
  min-height: 2.75rem;
  padding: 0.625rem 0.75rem;
  border: 0;
  border-radius: var(--coar-radius-s, 4px);
  background: transparent;
  color: var(--coar-text-neutral-secondary, #4b5563);
  font: inherit;
  font-size: 0.875rem;
  text-align: left;
  cursor: pointer;
}

.url-list-nav__item:hover {
  background: var(--coar-surface-neutral-hover, #f3f4f6);
}

.url-list-nav__item--active {
  background: var(--coar-surface-primary-subtle, #eff6ff);
  color: var(--coar-text-primary, #0369a1);
  font-weight: 600;
}

.url-list-nav__count {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  min-width: 1.5rem;
  height: 1.5rem;
  padding: 0 0.4rem;
  border-radius: 999px;
  background: var(--coar-surface-neutral-secondary, #e5e7eb);
  color: var(--coar-text-neutral-secondary, #4b5563);
  font-size: 0.75rem;
  font-weight: 600;
}

.url-list-nav__item--active .url-list-nav__count {
  background: var(--coar-surface-primary-muted, #dbeafe);
  color: inherit;
}

.url-configurator__panel {
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.flow-service-account {
  width: 100%;
  flex-shrink: 0;
}

.flow-terminal {
  display: flex;
  width: 100%;
  flex-shrink: 0;
  flex-direction: column;
  gap: 1rem;
  padding-top: 0.25rem;
}

.terminal-fields {
  padding: 1rem;
  border: 1px solid var(--coar-border-neutral-subtle, #d4d8e1);
  border-radius: var(--coar-radius-m, 4px);
}

.flex-section.flex-section--with-service-account {
  min-height: 15rem;
}

.service-account-picker {
  display: flex;
  align-items: flex-end;
  gap: 0.75rem;
  width: 100%;
}

.service-account-picker__select {
  flex: 1;
  min-width: 0;
}

.service-account-picker__create {
  flex-shrink: 0;
  margin-bottom: 0.2rem;
  white-space: nowrap;
}

.service-account-draft {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  min-height: 2.25rem;
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--coar-border-neutral-subtle, #d4d8e1);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
}

.service-account-draft__identity,
.service-account-draft__actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.service-account-draft__identity {
  min-width: 0;
}

.service-account-draft__text {
  display: flex;
  flex-direction: column;
  min-width: 0;
  line-height: 1.2;
}

.service-account-draft__text strong,
.service-account-draft__text span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.service-account-draft__text span {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.75rem;
}

.client-enabled-field {
  align-self: end;
  min-height: 2.5rem;
  display: flex;
  align-items: center;
}

.option-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 0.75rem;
}

.option-card {
  min-width: 0;
  padding: 0.75rem;
  border: 1px solid var(--coar-border-neutral-subtle, #d4d8e1);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
}

.field-label {
  display: block;
  margin-bottom: 0.35rem;
  font-size: 0.875rem;
  font-weight: 500;
  color: var(--coar-text-neutral-primary, #1f2937);
}

.credential-action {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
}

.tab-hint {
  font-size: 0.78rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-bottom: 4px;
}
.tab-hint--shortcut {
  opacity: 0.85;
}

.selection-help {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1rem;
  height: 1rem;
  padding: 0;
  border: 0;
  color: var(--coar-text-neutral-tertiary, #6b7280);
  background: transparent;
  cursor: help;
  line-height: 1;
  vertical-align: middle;
}

.selection-help:focus-visible {
  outline: 2px solid var(--coar-border-brand-primary, #009fe3);
  outline-offset: 2px;
  border-radius: 50%;
}

.selection-popover {
  width: min(30rem, calc(100vw - 2rem));
  padding: 1rem;
  color: var(--coar-text-neutral-primary, #1f2937);
}

:global(.coar-popover-panel:has(.selection-popover)) {
  --coar-popover-max-height: min(32rem, calc(100vh - 4rem));
  max-width: min(32rem, calc(100vw - 1.5rem));
}

.selection-popover h4,
.selection-popover h5,
.selection-popover p,
.selection-popover ul {
  margin: 0;
}

.selection-popover h4 {
  margin-bottom: 0.875rem;
  font-size: 0.95rem;
}

.selection-popover section + section {
  margin-top: 0.875rem;
  padding-top: 0.875rem;
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
}

.selection-popover h5 {
  margin-bottom: 0.35rem;
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.selection-popover p,
.selection-popover ul {
  font-size: 0.8rem;
  line-height: 1.45;
}

.selection-popover ul {
  display: grid;
  gap: 0.3rem;
  padding-left: 1.15rem;
}

.flow-issues-notice ul {
  margin: 0;
  padding-left: 1.15rem;
}

.field-hint {
  font-size: 0.72rem;
  line-height: 1.3;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-top: 4px;
}
.flex-section {
  flex: 1;
  display: flex;
  min-height: 20rem;
}

/* Content-appropriate input widths — never blindly width:100% so a
   12-char client_id doesn't sit in a 1000px wide box. Each class targets
   the wrapping CoarTextInput / CoarSelect; the inner DOM still does
   width:100% inside the bounded wrapper. */
.input-id :deep(input),
.input-id :deep(.coar-input) {
  max-width: 20rem;
}
.input-name :deep(input),
.input-name :deep(.coar-input) {
  max-width: 24rem;
}
.input-enum {
  max-width: 18rem;
}
.input-number {
  width: 100%;
}

.lifetime-form {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.lifetime-section {
  min-width: 0;
}

.section-divider__label {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.inline-info-popover {
  display: inline-flex;
  align-items: center;
  line-height: 1;
}

.section-divider__label,
.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.section-divider__label h3 {
  margin: 0;
  font: inherit;
}

.security-form {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.security-section {
  min-width: 0;
}

.security-policy-grid {
  display: grid;
  grid-template-columns: minmax(18rem, 1fr) minmax(28rem, 2fr);
  gap: 1rem;
  align-items: stretch;
}

.security-policy-card {
  min-width: 0;
  padding: 0.875rem;
  border: 1px solid var(--coar-border-neutral-subtle, #d4d8e1);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
}

.security-policy-card__heading {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  margin-bottom: 0.75rem;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.security-policy-card__heading h4 {
  margin: 0;
  font: inherit;
}

.security-policy-card__toggles {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.75rem 1.5rem;
}

.lifetime-grid {
  display: grid;
  gap: 0.75rem 1rem;
  justify-content: start;
}

.lifetime-grid--tokens {
  grid-template-columns: repeat(4, minmax(11rem, 15rem));
}

.lifetime-grid--sessions {
  grid-template-columns: repeat(2, minmax(11rem, 15rem));
}

.lifetime-field {
  width: 100%;
  min-width: 0;
}

.selection-popover--compact {
  width: min(26rem, calc(100vw - 2rem));
}

.selection-popover--compact p + p {
  margin-top: 0.75rem;
}

.textarea {
  width: 100%;
  padding: 8px 10px;
  border: 1px solid var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.8rem;
  resize: vertical;
}

@media (max-width: 900px) {
  .option-grid,
  .url-configurator,
  .security-policy-grid {
    grid-template-columns: 1fr;
  }

  .url-list-nav {
    flex-direction: row;
    overflow-x: auto;
    padding-right: 0;
    padding-bottom: 0.75rem;
    border-right: 0;
    border-bottom: 1px solid var(--coar-border-neutral, #e5e7eb);
  }

  .url-list-nav__item {
    width: auto;
    white-space: nowrap;
  }

  .lifetime-grid--tokens {
    grid-template-columns: repeat(2, minmax(11rem, 1fr));
  }

  .service-account-picker {
    align-items: stretch;
    flex-direction: column;
  }

  .service-account-picker__create {
    align-self: flex-start;
    margin-bottom: 0;
  }

  .service-account-draft {
    align-items: stretch;
    flex-direction: column;
  }

  .service-account-draft__actions {
    justify-content: flex-end;
  }
}
</style>
