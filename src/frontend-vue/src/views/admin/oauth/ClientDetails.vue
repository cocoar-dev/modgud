<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarTextInput,
  CoarPasswordInput,
  CoarNumberInput,
  CoarFormField,
  CoarSelect,
  CoarCheckbox,
  CoarButton,
  CoarTabGroup,
  CoarTab,
  CoarDualListbox,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import AppNote from '@/components/AppNote.vue'
import EditableStringList from '@/components/EditableStringList.vue'
import { useOAuthClientStore } from '@/stores/oauthClient.store'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import { useClone, CLIENT_CLONE } from '@/composables/useClone'
import type { OAuthClientDto, CreateOAuthClientDto, UpdateOAuthClientDto, AccessTokenType } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthClientStore()
const scopeStore = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const realmSettingsStore = useRealmSettingsStore()
const { consume } = useClone()
const isCreate = computed(() => props.id === 'create' && !justCreated.value)
// Genuinely-existing client opened from the list (drives the regenerate-secret
// affordance) — distinct from the transient just-created state where props.id
// is still 'create' but the client now exists.
const isExistingClient = computed(() => props.id !== 'create')
const loading = ref(false)
const error = ref<string | null>(null)
// "General" lives in the persistent left column (identity always visible)
// — tabs only host the multi-item editors that benefit from full width.
type ClientTab = 'apps' | 'scopes' | 'grants' | 'urls' | 'lifetimes' | 'dcr'
// Default to the Grants tab on create so the (now mandatory) grant choice is
// the first thing the admin sees.
const activeTab = ref<ClientTab>(props.id === 'create' ? 'grants' : 'apps')

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
  { value: 'systematic', label: 'Systematic' },
]

const accessTokenTypeOptions: { value: AccessTokenType; label: string }[] = [
  { value: 'Jwt', label: 'JWT (self-contained)' },
  { value: 'Reference', label: 'Reference (introspection)' },
]

const standardGrantTypeOptions = [
  { value: 'authorization_code', label: 'authorization_code',
    subtitle: 'Interactive user-flow with PKCE',
    icon: 'log-in', group: 'Standard flows' },
  { value: 'refresh_token', label: 'refresh_token',
    subtitle: 'Long-lived sessions, exchange refresh for new access token',
    icon: 'rotate-ccw', group: 'Standard flows' },
  { value: 'client_credentials', label: 'client_credentials',
    subtitle: 'Machine-to-machine, no user',
    icon: 'cpu', group: 'Standard flows' },
  { value: 'urn:ietf:params:oauth:grant-type:device_code', label: 'device_code',
    subtitle: 'TVs, CLIs, anything without a browser',
    icon: 'monitor', group: 'Standard flows' },
  // No implicit / password grants: OAuth 2.1 removes both and the backend
  // rejects them (OAuth.UnsupportedGrantType), so they are not offered here.
]

// Native passwordless grants (ADR-0010) — cookieless proofs exchanged
// directly at /connect/token. Only surfaced when the realm has enabled
// native grants (RealmSettings.NativeGrants.Enabled); a client also needs
// the matching gt:urn:cocoar:* permission to actually use them. Existing
// selections are kept visible even when the realm toggle is off so an
// already-configured grant never silently vanishes from the listbox.
const cocoarGrantTypeOptions = [
  { value: 'urn:cocoar:otp', label: 'urn:cocoar:otp',
    subtitle: 'Email one-time code, no browser redirect',
    icon: 'mail', group: 'Native passwordless (Cocoar)' },
  { value: 'urn:cocoar:magic', label: 'urn:cocoar:magic',
    subtitle: 'Magic-link token, no browser redirect',
    icon: 'link', group: 'Native passwordless (Cocoar)' },
  { value: 'urn:cocoar:passkey', label: 'urn:cocoar:passkey',
    subtitle: 'WebAuthn assertion, no browser redirect',
    icon: 'fingerprint', group: 'Native passwordless (Cocoar)' },
]

const nativeGrantsEnabled = computed(
  () => realmSettingsStore.settings?.NativeGrants?.Enabled ?? false)

// The inverse of nativeGrantsEnabled's info note: this client carries a native
// grant but the realm flag is OFF, so the grant silently won't work at
// /connect/token. The grant stays visible (see grantTypeOptions) precisely so an
// existing selection isn't hidden — which is exactly when this warning is needed.
const cocoarGrantValues = new Set(cocoarGrantTypeOptions.map((o) => o.value))
const hasNativeGrantWithRealmOff = computed(
  () => !nativeGrantsEnabled.value
    && form.value.AllowedGrantTypes.some((g) => cocoarGrantValues.has(g)))

const grantTypeOptions = computed(() => {
  const selected = new Set(form.value.AllowedGrantTypes)
  const cocoar = cocoarGrantTypeOptions.filter(
    (o) => nativeGrantsEnabled.value || selected.has(o.value))
  return [...standardGrantTypeOptions, ...cocoar]
})

const scopeOptions = computed(() => {
  const standardOidc = new Set(['openid', 'profile', 'email', 'roles', 'offline_access'])
  return scopeStore.scopes.map((s) => {
    const isStandard = standardOidc.has(s.Name) || !s.AppId
    const appLabel = s.AppId
      ? applicationsStore.apps.find((a) => a.Id === s.AppId)?.DisplayName ?? s.AppId
      : null
    const subtitleParts = [s.DisplayName, appLabel].filter(Boolean)
    return {
      value: s.Name,
      label: s.Name,
      subtitle: subtitleParts.length > 0 ? subtitleParts.join(' · ') : undefined,
      icon: 'tag',
      group: isStandard ? 'Realm-wide (OIDC standard)' : `App: ${appLabel ?? '—'}`,
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
  RequireClientSecret: boolean
  RequireConsent: boolean
  AllowRememberConsent: boolean
  AllowAccessTokensViaBrowser: boolean
  EnableLocalLogin: boolean
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
    // JWT default: most resource servers use AddJwtBearer which expects
    // a self-contained token. Reference is fine but requires the RS to
    // call /connect/introspect on every request, which most setups don't
    // wire up. JWT is the safer pick-by-default for the smoke flow.
    AccessTokenType: 'Jwt',
    RedirectUris: [],
    PostLogoutRedirectUris: [],
    AllowedGrantTypes: [],
    AllowedCorsOrigins: [],
    RequireClientSecret: true,
    RequireConsent: false,
    AllowRememberConsent: true,
    AllowAccessTokensViaBrowser: false,
    EnableLocalLogin: true,
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
  }
}

const form = ref<FormState>(emptyForm())
const original = ref<OAuthClientDto | null>(null)

function fromDto(dto: OAuthClientDto): FormState {
  return {
    ClientId: dto.ClientId,
    DisplayName: dto.DisplayName ?? '',
    ClientType: dto.ClientType,
    ConsentType: dto.ConsentType,
    ClientSecret: '',
    Enabled: dto.Enabled,
    Scopes: extractScopeNames(dto.Permissions),
    AccessTokenType: (dto.AccessTokenType as AccessTokenType) ?? 'Jwt',
    RedirectUris: [...(dto.RedirectUris ?? [])],
    PostLogoutRedirectUris: [...(dto.PostLogoutRedirectUris ?? [])],
    AllowedGrantTypes: [...(dto.AllowedGrantTypes ?? [])],
    AllowedCorsOrigins: [...(dto.AllowedCorsOrigins ?? [])],
    RequireClientSecret: dto.RequireClientSecret,
    RequireConsent: dto.RequireConsent,
    AllowRememberConsent: dto.AllowRememberConsent,
    AllowAccessTokensViaBrowser: dto.AllowAccessTokensViaBrowser,
    EnableLocalLogin: dto.EnableLocalLogin,
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
  }
}

const modalTitle = computed(() => {
  if (isCreate.value) return t('admin.oauthClients.createTitle', {}, 'Create OAuth Client')
  return form.value.DisplayName || form.value.ClientId
})

const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.ClientId)

// Create-time guard rails: a client with no grants can't mint any token, and an
// authorization_code client with no redirect URI can't complete the flow. Block
// Create until these are satisfied so the admin can't silently produce a dead
// client (the original bug: create only exposed identity fields).
const createBlockers = computed<string[]>(() => {
  if (!isCreate.value) return []
  const errs: string[] = []
  if (form.value.AllowedGrantTypes.length === 0)
    errs.push(t('admin.oauthClients.validation.noGrants', {}, 'Select at least one grant type (Grants tab) — without one the client cannot issue any tokens.'))
  if (form.value.AllowedGrantTypes.includes('authorization_code') && form.value.RedirectUris.length === 0)
    errs.push(t('admin.oauthClients.validation.noRedirect', {}, 'authorization_code needs at least one redirect URI (URLs tab).'))
  return errs
})

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
  return {
    visible: true,
    text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
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
  // Realm settings gate whether the native passwordless grants appear in the
  // grant-type picker. Cheap singleton GET; skip if already in the store.
  if (!realmSettingsStore.loaded) realmSettingsStore.load().catch(() => {})
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
  } finally {
    loading.value = false
  }
})

async function save() {
  if (!form.value.ClientId.trim()) return
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      const created = await store.create(buildCreateDto())
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
    RequireClientSecret: form.value.RequireClientSecret,
    RequireConsent: form.value.RequireConsent,
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
    RequireClientSecret: form.value.RequireClientSecret,
    RequireConsent: form.value.RequireConsent,
    AllowRememberConsent: form.value.AllowRememberConsent,
    AllowAccessTokensViaBrowser: form.value.AllowAccessTokensViaBrowser,
    EnableLocalLogin: form.value.EnableLocalLogin,
    RequirePushedAuthorizationRequests: form.value.RequirePushedAuthorizationRequests,
    RequireDpop: form.value.RequireDpop,
    RequireDpopNonce: form.value.RequireDpopNonce,
    IdentityTokenLifetime: form.value.IdentityTokenLifetime,
    AccessTokenLifetime: form.value.AccessTokenLifetime,
    AuthorizationCodeLifetime: form.value.AuthorizationCodeLifetime,
    SlidingRefreshTokenLifetime: form.value.SlidingRefreshTokenLifetime,
    ClientSessionIdleLifetime: form.value.ClientSessionIdleLifetime,
    ClientSessionAbsoluteLifetime: form.value.ClientSessionAbsoluteLifetime,
    ClearClientSessionIdleLifetime: form.value.ClientSessionIdleLifetime === null,
    ClearClientSessionAbsoluteLifetime: form.value.ClientSessionAbsoluteLifetime === null,
    // ADR-0009 PATCH: send the trimmed value verbatim — "" clears back to
    // realm-scoped, a host sets the per-client RP ID.
    WebAuthnRpId: form.value.WebAuthnRpId.trim(),
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
      <AppNote v-if="newSecret" variant="warning" :truncate="false" class="secret-banner">
        <div class="flex flex-col gap-2">
          <div class="font-medium">{{ t('admin.oauthClients.secretOnce', {}, 'Please copy the client secret now — it won\'t be shown again.') }}</div>
          <div class="flex items-center gap-2">
            <code class="flex-1 break-all rounded bg-white/40 px-2 py-1 text-xs">{{ newSecret }}</code>
            <CoarButton size="s" variant="secondary" icon-start="copy" @click="copySecret">
              {{ t('common.copy', {}, 'Copy') }}
            </CoarButton>
          </div>
        </div>
      </AppNote>

      <!-- Create-mode guard rails — a client with no grants / no redirect URI
           can't complete a flow. Surface the blockers so the admin knows to
           visit the Grants / URLs tabs before clicking Create. -->
      <AppNote v-if="isCreate && createBlockers.length" variant="info" :truncate="false" class="secret-banner">
        <ul class="blocker-list">
          <li v-for="msg in createBlockers" :key="msg">{{ msg }}</li>
        </ul>
      </AppNote>

      <!-- Server / validation error — surfaced at the top of the modal so it is
           visible regardless of the active tab or scroll position. The server
           sends an actionable message (e.g. "client_credentials must be linked
           to a ServiceAccount"); show it instead of a bare "HTTP 400". -->
      <AppNote v-if="error" variant="error" :truncate="false" class="secret-banner">
        {{ error }}
      </AppNote>

      <!-- Unified master-detail for both create + edit.
           Left = identity + status (always visible, never lost on tab switch).
           Right = the multi-item tabs that benefit from full width.
           In create mode the identity fields are editable and the tabs let the
           admin set grants / scopes / redirect-URIs up front, so the new client
           is born functional (the create form used to hide them entirely). -->
      <div class="two-col">
        <aside class="col-identity">
          <h3 class="col-heading">{{ t('admin.oauthClients.tabs.general', {}, 'General') }}</h3>
          <CoarFormField :label="t('admin.oauthClients.clientId', {}, 'Client ID')">
            <CoarTextInput v-model="form.ClientId" :disabled="!isCreate" :clearable="isCreate" class="input-id" />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.displayName', {}, 'Display Name')">
            <CoarTextInput v-model="form.DisplayName" clearable class="input-name" />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.type', {}, 'Client Type')">
            <CoarSelect v-model="form.ClientType" :options="clientTypeOptions" :disabled="!isCreate" class="input-enum" />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.consentType', {}, 'Consent Type')">
            <CoarSelect v-model="form.ConsentType" :options="consentTypeOptions" class="input-enum" />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.webAuthnRpId', {}, 'WebAuthn RP-ID (Passkeys)')"
            :hint="t('admin.oauthClients.webAuthnRpIdHint', {}, 'Optional. Dedicated relying-party domain for this app\'s native passkeys (e.g. app.example.com). Empty = realm domain. Warning: changing this invalidates all passkeys already registered for this app.')">
            <CoarTextInput v-model="form.WebAuthnRpId" clearable class="input-name"
              :placeholder="t('admin.oauthClients.webAuthnRpIdPlaceholder', {}, 'empty = realm domain')" />
          </CoarFormField>
          <CoarFormField v-if="isCreate" :label="t('admin.oauthClients.clientSecret', {}, 'Client Secret (empty = generate)')">
            <CoarPasswordInput v-model="form.ClientSecret" clearable class="input-name" />
          </CoarFormField>
          <div class="checkbox-stack">
            <CoarCheckbox v-model="form.Enabled" :label="t('admin.oauthClients.enabled', {}, 'Active')" />
            <CoarCheckbox v-model="form.RequireClientSecret" :label="t('admin.oauthClients.requireSecret', {}, 'Secret required')" />
            <CoarCheckbox v-model="form.RequireConsent" :label="t('admin.oauthClients.requireConsent', {}, 'Consent required')" />
            <div class="par-field">
              <CoarCheckbox v-model="form.RequirePushedAuthorizationRequests" :label="t('admin.oauthClients.requirePar', {}, 'Require Pushed Authorization Requests (PAR)')" />
              <p class="field-hint">
                {{ t('admin.oauthClients.requireParHint', {}, 'RFC 9126 — reject this client\'s direct /connect/authorize requests; parameters must be pushed through /connect/par first.') }}
              </p>
            </div>
            <div class="par-field">
              <CoarCheckbox v-model="form.RequireDpop" :label="t('admin.oauthClients.requireDpop', {}, 'Require DPoP')" />
              <p class="field-hint">
                {{ t('admin.oauthClients.requireDpopHint', {}, 'RFC 9449 — reject this client\'s token requests that carry no DPoP proof; the access token is bound to the proof key (cnf.jkt).') }}
              </p>
            </div>
            <div class="par-field">
              <CoarCheckbox v-model="form.RequireDpopNonce" :label="t('admin.oauthClients.requireDpopNonce', {}, 'Require DPoP nonce')" />
              <p class="field-hint">
                {{ t('admin.oauthClients.requireDpopNonceHint', {}, 'RFC 9449 — require a server-issued nonce in this client\'s DPoP proofs; the first proof is answered with use_dpop_nonce + a DPoP-Nonce header and the client retries.') }}
              </p>
            </div>
            <CoarCheckbox v-if="!isCreate" v-model="form.AllowRememberConsent" :label="t('admin.oauthClients.rememberConsent', {}, 'Remember consent')" />
            <CoarCheckbox v-if="!isCreate" v-model="form.AllowAccessTokensViaBrowser" :label="t('admin.oauthClients.tokensInBrowser', {}, 'Access tokens allowed in browser')" />
            <CoarCheckbox v-if="!isCreate" v-model="form.EnableLocalLogin" :label="t('admin.oauthClients.localLogin', {}, 'Local login allowed')" />
          </div>
          <CoarButton v-if="isExistingClient" size="s" variant="secondary" icon-start="rotate-ccw" :loading="loading" @click="regenerateSecret" class="regen-btn">
            {{ t('admin.oauthClients.regenerate', {}, 'Regenerate Client Secret') }}
          </CoarButton>
        </aside>

        <section class="col-tabs">
          <CoarTabGroup v-model="activeTab" class="tab-bar">
            <CoarTab id="apps">{{ t('admin.oauthClients.tabs.apps', {}, 'Apps') }}</CoarTab>
            <CoarTab id="scopes">{{ t('admin.oauthClients.tabs.scopes', {}, 'Scopes') }}</CoarTab>
            <CoarTab id="grants">{{ t('admin.oauthClients.tabs.grants', {}, 'Grants') }}</CoarTab>
            <CoarTab id="urls">{{ t('admin.oauthClients.tabs.urls', {}, 'URLs') }}</CoarTab>
            <CoarTab id="lifetimes">{{ t('admin.oauthClients.tabs.lifetimes', {}, 'Lifetimes') }}</CoarTab>
            <CoarTab v-if="original?.IsDynamicallyRegistered" id="dcr">
              {{ t('admin.oauthClients.tabs.dcr', {}, 'Registration Info') }}
            </CoarTab>
          </CoarTabGroup>

          <!-- Apps — link the client to one or more registered Applications.
               Empty selection = realm-wide (cross-app, OIDC standard scopes
               only). -->
          <div v-show="activeTab === 'apps'" class="tab-content">
            <p class="tab-hint">
              {{ t('admin.oauthClients.apps.hint', {}, 'Apps this client may operate in. Empty = realm-wide (only standard OIDC scopes). Multiple apps = Keycloak-style cross-app client.') }}
            </p>
            <p class="tab-hint tab-hint--shortcut">
              {{ t('admin.dualListbox.multiSelectHint', {}, 'Tip: Ctrl/Cmd-click for multi-select · Shift-click for a range · drag and drop between columns.') }}
            </p>
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
            <p class="tab-hint">
              {{ t('admin.oauthClients.scopes.hint', {}, 'OpenIddict rejects /connect/authorize and /connect/token requests for any scope not listed here. Add at minimum openid + roles for OIDC clients.') }}
            </p>
            <p class="tab-hint tab-hint--shortcut">
              {{ t('admin.dualListbox.multiSelectHint', {}, 'Tip: Ctrl/Cmd-click for multi-select · Shift-click for a range · drag and drop between columns.') }}
            </p>
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

          <!-- Grants — no silent defaults. Empty list produces a client that
               cannot mint any tokens. -->
          <div v-show="activeTab === 'grants'" class="tab-content">
            <p class="tab-hint">
              {{ t('admin.oauthClients.grantTypes.hint', {}, 'No silent defaults: leaving this empty produces a client that cannot mint tokens. SPAs / mobile apps: authorization_code + refresh_token. Server-to-server: client_credentials. Pick what the client actually needs.') }}
            </p>
            <p class="tab-hint tab-hint--shortcut">
              {{ t('admin.dualListbox.multiSelectHint', {}, 'Tip: Ctrl/Cmd-click for multi-select · Shift-click for a range · drag and drop between columns.') }}
            </p>
            <AppNote v-if="nativeGrantsEnabled" variant="info">
              {{ t('admin.oauthClients.grantTypes.nativeHintShort', {}, 'Passwordless grants are enabled for this realm — add one to allow it for this client.') }}
              <template #details>
                {{ t('admin.oauthClients.grantTypes.nativeHint', {}, 'Native passwordless grants (urn:cocoar:otp / :magic / :passkey) are enabled for this realm and available below. Add one here to give this client the matching gt:urn:cocoar:* permission — only then can it exchange a passwordless proof at /connect/token.') }}
              </template>
            </AppNote>
            <AppNote v-if="hasNativeGrantWithRealmOff" variant="warning">
              {{ t('admin.oauthClients.grantTypes.nativeDisabledWarningShort', {}, 'A native grant is selected but disabled for this realm — it will not work.') }}
              <template #details>
                {{ t('admin.oauthClients.grantTypes.nativeDisabledWarning', {}, 'This client has a native passwordless grant (urn:cocoar:otp / :magic / :passkey) selected, but native grants are DISABLED for this realm — so it will not work: the token endpoint rejects the grant and the OTP-request endpoint returns an error instead of emailing a code. Enable them under Realm Settings → Native Passwordless Grants.') }}
              </template>
            </AppNote>
            <section class="flex-section">
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
          </div>

          <!-- URLs -->
          <div v-show="activeTab === 'urls'" class="tab-content">
            <CoarFormField :label="t('admin.oauthClients.redirectUris', {}, 'Redirect-URIs')">
              <EditableStringList
                v-model="form.RedirectUris"
                :placeholder="t('admin.oauthClients.redirectUri.placeholder', {}, 'https://app.example.com/signin-oidc')" />
            </CoarFormField>
            <CoarFormField :label="t('admin.oauthClients.postLogoutRedirectUris', {}, 'Post-Logout Redirect-URIs')">
              <EditableStringList
                v-model="form.PostLogoutRedirectUris"
                :placeholder="t('admin.oauthClients.postLogoutRedirectUri.placeholder', {}, 'https://app.example.com/signout-callback-oidc')" />
            </CoarFormField>
            <CoarFormField
              :label="t('admin.oauthClients.accessTokenType', {}, 'Access Token Type')"
              :hint="t('admin.oauthClients.accessTokenType.hint', {}, 'JWT: token is self-contained, the resource server validates it locally via signature. Reference: token is opaque, the RS must call /connect/introspect on every request. JWT is the right pick for AddJwtBearer-based RSes.')">
              <CoarSelect v-model="form.AccessTokenType" :options="accessTokenTypeOptions" class="input-enum" />
            </CoarFormField>
            <CoarFormField :label="t('admin.oauthClients.corsOrigins', {}, 'CORS Origins')">
              <EditableStringList
                v-model="form.AllowedCorsOrigins"
                :placeholder="t('admin.oauthClients.corsOrigin.placeholder', {}, 'https://app.example.com')" />
            </CoarFormField>
          </div>

          <!-- Lifetimes -->
          <div v-show="activeTab === 'lifetimes'" class="tab-content">
            <p class="text-xs text-gray-500 mb-2">
              {{ t('admin.oauthClients.lifetimesHint', {}, 'Values in seconds. Empty token values use the IdP default; empty session values inherit from the linked App and then the Realm.') }}
            </p>
            <div class="lifetime-grid">
              <CoarFormField :label="t('admin.oauthClients.identityTokenLifetime', {}, 'Identity-Token')">
                <CoarNumberInput v-model="form.IdentityTokenLifetime" clearable class="input-number" />
              </CoarFormField>
              <CoarFormField :label="t('admin.oauthClients.accessTokenLifetime', {}, 'Access-Token')">
                <CoarNumberInput v-model="form.AccessTokenLifetime" clearable class="input-number" />
              </CoarFormField>
              <CoarFormField :label="t('admin.oauthClients.authCodeLifetime', {}, 'Authorization-Code')">
                <CoarNumberInput v-model="form.AuthorizationCodeLifetime" clearable class="input-number" />
              </CoarFormField>
              <CoarFormField :label="t('admin.oauthClients.slidingRefreshLifetime', {}, 'Sliding Refresh-Token')">
                <CoarNumberInput v-model="form.SlidingRefreshTokenLifetime" clearable class="input-number" />
              </CoarFormField>
            </div>
            <AppNote variant="info">
              {{ t('admin.oauthClients.clientSessionsHintShort', {}, 'Client sessions govern refresh-token use — idle lifetime slides, absolute lifetime is fixed.') }}
              <template #details>
                {{ t('admin.oauthClients.clientSessionsHint', {}, 'Client sessions are authoritative for refresh-token use. Idle lifetime slides when the app refreshes; absolute lifetime never slides. Maximum: 315,360,000 seconds (3650 days / 10 years). Access tokens remain short-lived independently.') }}
              </template>
            </AppNote>
            <div class="lifetime-grid">
              <CoarFormField :label="t('admin.oauthClients.clientSessionIdleLifetime', {}, 'Client session idle lifetime')">
                <CoarNumberInput v-model="form.ClientSessionIdleLifetime" clearable class="input-number" />
              </CoarFormField>
              <CoarFormField :label="t('admin.oauthClients.clientSessionAbsoluteLifetime', {}, 'Client session absolute lifetime')">
                <CoarNumberInput v-model="form.ClientSessionAbsoluteLifetime" clearable class="input-number" />
              </CoarFormField>
            </div>
          </div>

          <!-- Registration Info — DCR clients only -->
          <div v-show="activeTab === 'dcr' && original?.IsDynamicallyRegistered" class="tab-content">
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
        </section>
      </div>
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

.blocker-list {
  margin: 0;
  padding-left: 1.1rem;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}
.checkbox-stack {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  margin-top: 0.25rem;
}
/* Group the PAR checkbox with its helper text so the stack's gap doesn't
   orphan the hint between two checkboxes. */
.par-field {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
}

/* EDIT mode — master-detail. Identity stays visible while tabs change. */
.two-col {
  flex: 1;
  display: grid;
  grid-template-columns: 24rem 1fr;
  /* Bind the single implicit row to the flex-allocated height. Without an
     explicit rows track the row is `auto` and sizes to the taller column's
     content, so it can overgrow the modal body — then `.col-identity`'s
     overflow never engages and its last element (the Regenerate-Secret
     button) is pushed under the sticky footer, unreachable by scroll.
     `minmax(0, 1fr)` clamps the row so each column scrolls independently. */
  grid-template-rows: minmax(0, 1fr);
  gap: 1.25rem;
  min-height: 0;
  min-width: 0;
}
.col-identity {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  padding-right: 1.25rem;
  /* Bottom breathing room so the last control (Regenerate Secret) clears the
     scroll-area edge instead of sitting flush against it. */
  padding-bottom: 0.5rem;
  border-right: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  overflow-y: auto;
  min-height: 0;
}

/* This is a scroll container: its children must keep their natural height and
   let the column scroll — never be flex-shrunk to fit. Without this the last
   item (the Regenerate-Secret button, whose overflow:hidden gives it a 0
   min-height) is the most shrinkable child, so an overflowing column crushes
   it to height:0 and it vanishes (it only reappeared when zoomed out far
   enough that the content stopped overflowing). */
.col-identity > * {
  flex-shrink: 0;
}
.col-heading {
  margin: 0 0 0.25rem 0;
  font-size: 0.78rem;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.regen-btn {
  margin-top: 0.5rem;
  align-self: flex-start;
}

.col-tabs {
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
}
.tab-bar {
  flex-shrink: 0;
  margin-bottom: 12px;
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
.tab-hint {
  font-size: 0.78rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  margin-bottom: 4px;
}
.tab-hint--shortcut {
  opacity: 0.85;
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
  min-height: 22rem;
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
.input-number :deep(input),
.input-number :deep(.coar-input) {
  max-width: 8rem;
}

/* Lifetime tab grid — five short number fields, two columns, packed. */
.lifetime-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(18rem, 1fr));
  gap: 0.75rem;
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
</style>
