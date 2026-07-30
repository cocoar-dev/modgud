<script setup lang="ts">
import { ref, computed, onMounted, watch, toRaw } from 'vue'
import {
  CoarNotice,
  CoarTextInput, CoarPasswordInput, CoarNumberInput, CoarFormField, CoarCheckbox, CoarTabGroup, CoarTab,
  CoarButton, CoarIcon, CoarSelect, CoarDivider, CoarPopover,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation } from '@cocoar/vue-fragment-parser'
import ModalLayout from '@/components/ModalLayout.vue'
import ColorField from '@/components/ColorField.vue'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import type { FlavorConfigFieldDto, FlavorDto, LoginProviderDto } from '@/models/loginProvider'
import UserUpdateScriptEditor from './UserUpdateScriptEditor.vue'
import FlavorConnectionPanel from './panels/FlavorConnectionPanel.vue'
import ClaimMapEditor from './panels/ClaimMapEditor.vue'

const { t } = useI18n()
const store = useLoginProviderStore()
const { navigateToModal } = useFragmentNavigation()

const props = defineProps<{ id: string; close: (result?: unknown) => void }>()
const isCreate = computed(() => props.id === 'create')

const activeTab = ref<'general' | 'connection' | 'advanced' | 'claim-mapping' | 'claims' | 'linking'>('general')
const activeClaimMap = ref<'attributes' | 'amr'>('attributes')
const loading = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)

const provider = ref<LoginProviderDto | null>(null)
// Flavor selected in the picker — drives Type, defaults, and ConfigSchema. In
// Edit mode it is locked to the existing provider's Flavor (Type/Flavor are
// immutable post-create). In Add mode the admin can switch and the body morphs.
const flavorKey = ref<string>('')
const flavor = computed<FlavorDto | null>(() =>
  store.flavors.find((f) => f.Key === flavorKey.value) ?? null
)

const isBuiltIn = computed(() => provider.value?.IsBuiltIn === true)
const isInternal = computed(() => provider.value?.Type === 'Internal')
const isSaml = computed(() =>
  isCreate.value
    ? flavor.value?.Type === 'Saml'
    : provider.value?.Type === 'Saml'
)

interface FormState {
  Slug: string
  DisplayName: string
  Description: string
  ClientId: string
  Scopes: string[]
  UserUpdateScript: string
  StoreRawClaims: boolean
  RawClaimsRetentionDays: number | null
  AutoCreateUsers: boolean
  AllowLinking: boolean
  TrustForEmailLink: boolean
  TrustForAuthorization: boolean
  AuthoritativeForProfile: boolean
  AllowedEmailDomains: string[]
  IconName: string
  ButtonColorHex: string
  FlavorData: Record<string, unknown>
  /** Staged enabled state — committed with Save (not sent on toggle). */
  Enabled: boolean
}

function emptyForm(): FormState {
  return {
    Slug: '',
    DisplayName: '',
    Description: '',
    ClientId: '',
    Scopes: [],
    UserUpdateScript: '',
    StoreRawClaims: false,
    RawClaimsRetentionDays: null,
    AutoCreateUsers: false,
    AllowLinking: true,
    TrustForEmailLink: false,
    TrustForAuthorization: false,
    AuthoritativeForProfile: false,
    AllowedEmailDomains: [],
    IconName: '',
    ButtonColorHex: '',
    FlavorData: {},
    Enabled: false,
  }
}

const form = ref<FormState>(emptyForm())
const newSecret = ref<string>('')

// Slug grammar mirrors LoginProviderSlugRules on the backend: 3-64 chars,
// lowercase letters/digits/hyphens, starts with a letter, ends alphanumeric.
const SLUG_RE = /^[a-z][a-z0-9-]{1,62}[a-z0-9]$/

// Derive a slug suggestion from a display name: lowercase, non-alphanumerics
// to hyphens, collapse + trim hyphens, ensure it starts with a letter.
function slugify(name: string): string {
  let s = name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-+|-+$/g, '')
  s = s.replace(/^[^a-z]+/, '') // first char must be a letter
  return s.slice(0, 64).replace(/-+$/, '')
}

// Tracks whether the admin has hand-edited the slug. While false (and in Add
// mode), the slug is kept in sync with the Display Name live as they type.
// Clearing the slug field re-arms the auto-sync.
const slugManuallyEdited = ref(false)

// User typed in the slug field directly. Stop auto-deriving — unless they
// cleared it, in which case re-arm so a further Display-Name edit refills it.
function onSlugInput(value: string) {
  form.value.Slug = value
  slugManuallyEdited.value = value.trim().length > 0
}

// Live-derive the slug from the Display Name (Add mode, until hand-edited).
watch(
  () => form.value.DisplayName,
  (name) => {
    if (!isCreate.value || slugManuallyEdited.value) return
    form.value.Slug = slugify(name)
  },
)

const modalTitle = computed(() => {
  if (isCreate.value) return t('admin.loginProviders.createTitle', {}, 'Add Login Provider')
  return provider.value?.DisplayName || ''
})

// Provider callback URLs depend only on the slug — the rest is the host the
// request comes in on (matches the runtime handlers, which use req.Host). So we
// show them live from the slug field, before save, instead of waiting for the
// backend DTO (which derives them from configured PublicUrl and can show an
// internal/unreachable host). Only shown for a valid slug.
const urlSlug = computed(() => {
  const s = form.value.Slug.trim()
  return SLUG_RE.test(s) ? s : ''
})
// OIDC callback (/signin-oidc/{slug}) — the value the admin pastes into the IdP
// app registration's redirect-URI list.
const redirectUri = computed(() =>
  urlSlug.value ? `${window.location.origin}/signin-oidc/${urlSlug.value}` : '',
)
const samlSpMetadataUrl = computed(() =>
  urlSlug.value ? `${window.location.origin}/saml/${urlSlug.value}/sp-metadata` : '',
)
const samlAcsUrl = computed(() =>
  urlSlug.value ? `${window.location.origin}/saml/${urlSlug.value}/acs` : '',
)

const slugHint = computed(() => {
  if (!isCreate.value) {
    return t(
      'admin.loginProviders.slugHintEdit',
      {},
      'URL-Kennung des Providers. Zum Ändern muss der Provider neu angelegt werden.',
    )
  }

  return isSaml.value
    ? t(
        'admin.loginProviders.slugHintCreateSaml',
        {},
        'Teil der SAML-Endpunkte, z. B. „/saml/<slug>/acs“. Nach dem Anlegen nicht mehr änderbar.',
      )
    : t(
        'admin.loginProviders.slugHintCreateOidc',
        {},
        'Teil der OIDC-Callback-URL „/signin-oidc/<slug>“. Nach dem Anlegen nicht mehr änderbar.',
      )
})

// True when the selected flavor exposes any advanced-section fields (all SAML
// flavors do; OIDC flavors don't today) — gates the "Advanced" tab.
const hasAdvancedFields = computed(() =>
  (flavor.value?.ConfigSchema ?? []).some((f) => (f.Section ?? 'connection') === 'advanced'),
)

// Re-init the claim-map editors when the provider/flavor identity changes
// (load / flavor switch), without looping on every keystroke.
const claimReloadKey = computed(() => `${flavorKey.value}:${provider.value?.Id ?? 'new'}`)
const attributeMapCount = computed(() =>
  Object.keys((form.value.FlavorData.attributeMap as Record<string, string[]> | undefined) ?? {}).length
)
const amrMappingCount = computed(() =>
  Object.keys((form.value.FlavorData.amrMapping as Record<string, string[]> | undefined) ?? {}).length
)

const loginButtonPreviewStyle = computed(() => {
  const color = form.value.ButtonColorHex.trim()
  const match = /^#([0-9a-f]{6})$/i.exec(color)
  if (!match) return undefined

  const hex = match[1]!
  const red = Number.parseInt(hex.slice(0, 2), 16)
  const green = Number.parseInt(hex.slice(2, 4), 16)
  const blue = Number.parseInt(hex.slice(4, 6), 16)
  // A lightweight luminance check keeps the preview label readable for both
  // light and dark brand colours without changing the configured colour.
  const luminance = (red * 299 + green * 587 + blue * 114) / 1000

  return {
    backgroundColor: color,
    borderColor: color,
    color: luminance < 150 ? '#ffffff' : '#111827',
  }
})


// Flavor picker options. Grouped header label keeps the dropdown scan-friendly
// once both OIDC + SAML flavors are in the list (today: 2 OIDC + 3 SAML).
const flavorOptions = computed(() => {
  const oidc = store.flavors.filter((f) => f.Type === 'Oidc')
  const saml = store.flavors.filter((f) => f.Type === 'Saml')
  const opts: { value: string; label: string }[] = []
  for (const f of oidc) opts.push({ value: f.Key, label: `OIDC · ${f.DisplayName}` })
  for (const f of saml) opts.push({ value: f.Key, label: `SAML · ${f.DisplayName}` })
  return opts
})

// Hydrate the form from the selected flavor's defaults. Called on Add-modal
// open and whenever the admin switches flavor in Add mode. Only re-seeds the
// flavor-derived fields; admin-typed identity (DisplayName/Description/ClientId/
// ButtonColorHex/AllowedEmailDomains) is preserved across flavor switches.
function reseedFromFlavor(key: string) {
  const f = store.flavors.find((ff) => ff.Key === key)
  if (!f) return
  form.value.Scopes = [...f.DefaultScopes]
  form.value.UserUpdateScript = f.DefaultUserUpdateScript
  form.value.StoreRawClaims = f.DefaultStoreRawClaims
  form.value.IconName = f.DefaultIconName
  // ConfigSchema differs per flavor — start clean, but seed any field defaults
  // (e.g. SAML signing toggles) so checkboxes reflect the real default instead
  // of rendering unchecked while the backend would apply a different default.
  // Pinia exposes nested flavor defaults through Vue's reactive proxy. Passing
  // that proxy directly to structuredClone throws a DataCloneError, which used
  // to abort every switch to a SAML flavor (OIDC defaults are null and therefore
  // hid the bug). Unwrap the store value before cloning the JSON-compatible data.
  const seeded: Record<string, unknown> = normalizeFlavorData(
    structuredClone(toRaw(f.DefaultFlavorData ?? {})),
    f.ConfigSchema,
  )
  for (const field of f.ConfigSchema) {
    if (!(field.Key in seeded) && field.Default !== undefined && field.Default !== null) {
      seeded[field.Key] = field.Default
    }
  }
  form.value.FlavorData = seeded
}

async function load() {
  loading.value = true
  try {
    await store.initialize()
    if (isCreate.value) {
      flavorKey.value = store.flavors[0]?.Key ?? ''
      provider.value = null
      form.value = emptyForm()
      reseedFromFlavor(flavorKey.value)
      return
    }
    const existing = store.providers.find((p) => p.Id === props.id)
      ?? await store.loadOne(props.id)
    if (!existing) {
      error.value = t('admin.loginProviders.notFound', {}, 'Not found')
      return
    }
    provider.value = existing
    flavorKey.value = existing.Flavor
    const loadedFlavor = store.flavors.find((f) => f.Key === existing.Flavor) ?? null
    form.value = {
      Slug: existing.Slug,
      DisplayName: existing.DisplayName,
      Description: existing.Description ?? '',
      ClientId: existing.ClientId,
      Scopes: [...existing.Scopes],
      UserUpdateScript: existing.UserUpdateScript,
      StoreRawClaims: existing.StoreRawClaims,
      RawClaimsRetentionDays: existing.RawClaimsRetentionDays ?? null,
      AutoCreateUsers: existing.AutoCreateUsers,
      AllowLinking: existing.AllowLinking,
      TrustForEmailLink: existing.TrustForEmailLink,
      TrustForAuthorization: existing.TrustForAuthorization,
      AuthoritativeForProfile: existing.AuthoritativeForProfile,
      AllowedEmailDomains: existing.AllowedEmailDomains ? [...existing.AllowedEmailDomains] : [],
      IconName: existing.IconName ?? '',
      ButtonColorHex: existing.ButtonColorHex ?? '',
      FlavorData: normalizeFlavorData(existing.FlavorData, loadedFlavor?.ConfigSchema),
      Enabled: existing.Enabled,
    }
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

// Re-seed defaults on flavor switch in Add mode. Edit mode is locked (Flavor
// immutable post-create) so the watch is a no-op there.
watch(flavorKey, (newKey, oldKey) => {
  if (!isCreate.value || !newKey || newKey === oldKey) return
  reseedFromFlavor(newKey)
  activeClaimMap.value = 'attributes'
  if (activeTab.value === 'claim-mapping' && flavor.value?.Type !== 'Saml') {
    activeTab.value = 'connection'
  } else if (activeTab.value === 'advanced' && !hasAdvancedFields.value) {
    activeTab.value = 'connection'
  }
})

const displayNameError = computed(() =>
  form.value.DisplayName.trim()
    ? ''
    : t('admin.loginProviders.validation.displayName', {}, 'Display Name ist erforderlich.')
)

const slugError = computed(() =>
  !isCreate.value || SLUG_RE.test(form.value.Slug.trim())
    ? ''
    : t('admin.loginProviders.validation.slug', {}, 'Ein gültiger Slug ist erforderlich.')
)

const generalIssues = computed(() =>
  [displayNameError.value, slugError.value].filter((issue) => issue.length > 0)
)

function missingSchemaFields(section: string) {
  return (flavor.value?.ConfigSchema ?? [])
    .filter((field) => (field.Section ?? 'connection') === section && field.Required)
    .filter((field) => {
      const value = form.value.FlavorData[field.Key]
      return value === undefined || value === null || (typeof value === 'string' && value.trim() === '')
    })
}

const hasSamlMetadata = computed(() => {
  const metadataUrl = String(form.value.FlavorData.MetadataUrl ?? '').trim()
  const metadataXml = String(form.value.FlavorData.MetadataXml ?? '').trim()
  return metadataUrl.length > 0 || metadataXml.length > 0
})

const clientIdError = computed(() =>
  flavor.value?.Type === 'Oidc' && !form.value.ClientId.trim()
    ? t('admin.loginProviders.validation.clientId', {}, 'Client-ID fehlt.')
    : ''
)

const hasOidcSecret = computed(() =>
  isCreate.value
    ? newSecret.value.trim().length > 0
    : provider.value?.HasClientSecret === true || newSecret.value.trim().length > 0
)

const clientSecretError = computed(() =>
  flavor.value?.Type === 'Oidc' && !hasOidcSecret.value
    ? t('admin.loginProviders.validation.clientSecret', {}, 'Client-Secret fehlt.')
    : ''
)

const samlMetadataError = computed(() =>
  flavor.value?.Type === 'Saml' && !hasSamlMetadata.value
    ? t('admin.loginProviders.validation.samlMetadata', {}, 'IdP-Metadaten fehlen.')
    : ''
)

const connectionFieldErrors = computed<Record<string, string>>(() => {
  if (!samlMetadataError.value) return {} as Record<string, string>
  return {
    MetadataUrl: samlMetadataError.value,
    MetadataXml: samlMetadataError.value,
  }
})

const connectionIssues = computed(() => {
  const issues = missingSchemaFields('connection').map((field) =>
    t('admin.loginProviders.validation.requiredField', { field: field.Label }, '{field} ist erforderlich.')
  )
  if (flavor.value?.Type === 'Oidc') {
    if (clientIdError.value) issues.push(clientIdError.value)
    if (clientSecretError.value) issues.push(clientSecretError.value)
  } else if (samlMetadataError.value) {
    issues.push(samlMetadataError.value)
  }
  return issues
})

const tabIssue = (tab: 'general' | 'connection') =>
  tab === 'general' ? generalIssues.value : connectionIssues.value

async function save() {
  if (!form.value.DisplayName.trim()) {
    error.value = t('admin.loginProviders.nameRequired', {}, 'Name is required')
    return
  }
  // Slug is required + format-checked at create only (immutable afterwards).
  if (isCreate.value && !SLUG_RE.test(form.value.Slug.trim())) {
    error.value = t('admin.loginProviders.slugInvalid', {},
      'Slug must be 3-64 characters: lowercase letters, digits, hyphens; must start with a letter and end alphanumerically.')
    return
  }
  error.value = null
  saving.value = true
  try {
    if (isCreate.value) {
      await createProvider()
    } else {
      await updateProvider()
    }
  } catch (e: any) {
    error.value = e?.response?.data?.Message ?? e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

async function createProvider() {
  if (!flavor.value) {
    error.value = t('admin.loginProviders.flavorRequired', {}, 'Select flavor')
    return
  }

  // Required-field check on the ConfigSchema BEFORE we hit the backend —
  // only for OIDC, where the backend's flavor.DeriveEndpoints throws on
  // empty Required fields. The old two-step List dialog auto-seeded
  // placeholder GUIDs for that case so Create couldn't fail validation;
  // the single-modal Add path doesn't, so an admin who saves from the
  // Allgemein tab without visiting Verbindung would get a cryptic backend
  // FlavorDataInvalid. Auto-switch to Verbindung and point at the missing
  // fields instead.
  //
  // SAML is intentionally permissive at Create-time: CreateSamlAsync
  // accepts an empty FlavorData and lands the provider disabled, so the
  // admin can paste IdP metadata + enable later. Don't apply the gate
  // there.
  if (flavor.value.Type === 'Oidc') {
    const missing = missingSchemaFields('connection')
    if (missing.length > 0) {
      activeTab.value = 'connection'
      const names = missing.map((f) => f.Label).join(', ')
      error.value = t(
        'admin.loginProviders.requiredFieldsMissing', {},
        `Required fields missing in the Connection tab: ${names}`
      )
      return
    }
  }
  if (form.value.Enabled && connectionIssues.value.length > 0) {
    activeTab.value = 'connection'
    error.value = t(
      'admin.loginProviders.validation.notReady',
      { issues: connectionIssues.value.join(' · ') },
      `Der Provider kann noch nicht aktiviert werden: ${connectionIssues.value.join(' · ')}`
    )
    return
  }

  const created = await store.create({
    Flavor: flavorKey.value,
    DisplayName: form.value.DisplayName.trim(),
    Slug: form.value.Slug.trim(),
    // Type follows the chosen flavor — OIDC flavors create OIDC providers,
    // SAML flavors create SAML providers. Backend re-validates.
    Type: flavor.value.Type,
    Description: form.value.Description.trim() || null,
    FlavorData: form.value.FlavorData,
    Enabled: form.value.Enabled,
    ClientId: form.value.ClientId.trim() || null,
    InitialClientSecret: flavor.value.Type === 'Oidc' ? (newSecret.value.trim() || null) : null,
    Scopes: form.value.Scopes,
    UserUpdateScript: form.value.UserUpdateScript,
    StoreRawClaims: form.value.StoreRawClaims,
    RawClaimsRetentionDays: form.value.RawClaimsRetentionDays,
    AutoCreateUsers: form.value.AutoCreateUsers,
    AllowLinking: form.value.AllowLinking,
    TrustForEmailLink: form.value.TrustForEmailLink,
    TrustForAuthorization: form.value.TrustForAuthorization,
    AuthoritativeForProfile: form.value.AuthoritativeForProfile,
    AllowedEmailDomains: form.value.AllowedEmailDomains.length > 0 ? form.value.AllowedEmailDomains : null,
    IconName: form.value.IconName || null,
    ButtonColorHex: form.value.ButtonColorHex || null,
  })

  newSecret.value = ''

  // Transition Add → Edit in-place: re-route the modal's fragment so a refresh
  // re-opens in the right mode, and let the regular load() pick up the new doc.
  provider.value = created
  flavorKey.value = created.Flavor
  navigateToModal(created.Id)
}

async function updateProvider() {
  if (!provider.value) return
  if (isBuiltIn.value) return // defensive — Save button is hidden, but never trust UI alone
  await store.update(provider.value.Id, {
    DisplayName: form.value.DisplayName.trim(),
    Description: form.value.Description.trim() || null,
    ClientId: form.value.ClientId.trim(),
    Scopes: form.value.Scopes,
    UserUpdateScript: form.value.UserUpdateScript,
    StoreRawClaims: form.value.StoreRawClaims,
    RawClaimsRetentionDays: form.value.RawClaimsRetentionDays,
    AutoCreateUsers: form.value.AutoCreateUsers,
    AllowLinking: form.value.AllowLinking,
    TrustForEmailLink: form.value.TrustForEmailLink,
    TrustForAuthorization: form.value.TrustForAuthorization,
    AuthoritativeForProfile: form.value.AuthoritativeForProfile,
    AllowedEmailDomains: form.value.AllowedEmailDomains.length > 0 ? form.value.AllowedEmailDomains : null,
    IconName: form.value.IconName || null,
    ButtonColorHex: form.value.ButtonColorHex || null,
    FlavorData: form.value.FlavorData,
    // Staged enabled state commits here. The backend runs the readiness gate
    // against the merged values, so enabling while setting metadata in the same
    // save passes; a rejected enable surfaces as a save error.
    Enabled: form.value.Enabled,
  })
  props.close()
}

async function rotateSecret() {
  if (!provider.value || !newSecret.value) return
  saving.value = true
  error.value = null
  try {
    await store.rotateSecret(provider.value.Id, newSecret.value)
    newSecret.value = ''
    const reloaded = await store.loadOne(provider.value.Id)
    if (reloaded) provider.value = reloaded
  } catch (e: any) {
    error.value = e?.response?.data?.Message ?? e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

function copyRedirect() {
  if (redirectUri.value) navigator.clipboard?.writeText(redirectUri.value)
}

function copyText(s: string) {
  if (s) navigator.clipboard?.writeText(s)
}

const footerButton = computed(() => ({
  // Visible in Add mode (no provider yet) and in Edit mode for non-built-in
  // providers. Built-in (Internal seed) stays read-only.
  visible: isCreate.value || !isBuiltIn.value,
  text: isCreate.value
    ? t('common.create', {}, 'Create')
    : t('common.save', {}, 'Save'),
  disabled: saving.value || loading.value,
  onClick: save,
}))

onMounted(load)

function parseList(s: string): string[] {
  return s.split(/[\s,]+/).map((p) => p.trim()).filter(Boolean)
}

// FlavorData casing reconciliation: ConfigSchema field Keys are PascalCase
// (e.g. `MetadataUrl`) to match the OIDC convention surfaced in the admin UI,
// but SamlFlavorData.ToJson serialises camelCase (e.g. `metadataUrl`). When
// the modal reloads an existing provider, existing.FlavorData arrives in
// camelCase form — if we just spread that into the form, FlavorConnectionPanel
// (which reads modelValue[field.Key] = PascalCase) would render every field
// empty, and the admin's edit would silently create a parallel PascalCase
// key. Without the SamlFlavorData backend tie-breaker the camelCase stale
// value would then overwrite the edit.
//
// Normalise on load: for every schema field that exists in the data under a
// camelCase key, promote it to the PascalCase key (and drop the camelCase
// variant). Schema-unknown keys are preserved as-is for forwards-compat.
function normalizeFlavorData(
  raw: Record<string, unknown> | null | undefined,
  schema: FlavorConfigFieldDto[] | undefined,
): Record<string, unknown> {
  if (!raw) return {}
  const out: Record<string, unknown> = { ...raw }
  for (const field of schema ?? []) {
    const pascal = field.Key
    if (pascal.length === 0) continue
    const camel = pascal.charAt(0).toLowerCase() + pascal.slice(1)
    if (pascal === camel) continue
    if (camel in out && !(pascal in out)) {
      out[pascal] = out[camel]
      delete out[camel]
    }
  }
  return out
}

// Tab visibility — Internal-typed providers only show the General tab, since
// none of the OIDC/SAML concerns (connection, claims, linking) apply. Internal
// is also not selectable in the Add picker (flavorOptions filters it), so this
// only matters when editing the seeded Internal provider.
const showProtocolTabs = computed(() => !isInternal.value)
// SAML providers don't have ClientId / Scopes / Client-Secret — those live on
// the OIDC side only. The Verbindung tab hides them when the flavor is SAML.
const showOidcConnectionFields = computed(() => !isSaml.value)
</script>

<template>
  <ModalLayout
    :close="close"
    :title="modalTitle"
    icon="log-in"
    :footer-button="footerButton"
    :readonly="isBuiltIn"
  >
    <!--
      Built-in provider: hard-block on edits via UI, backend rejects too. The
      statement covers the entire modal, so it is a banner pinned under the
      header rather than a note floating above the first section.

      `:readonly` so this modal carries the same "read-only" tag in the title
      bar as every other read-only modal — the block was hand-rolled here (Save
      hidden + :disabled on every field), which worked but left the header
      silent. Tag = the state, banner = the reason.
    -->
    <template #banner>
      <CoarNotice placement="banner" v-if="isBuiltIn" variant="info"
        :label="t('common.systemManagedLabel', {}, 'System')">
        {{ t('common.systemManaged', {}, 'Cannot be changed.') }}
      </CoarNotice>
    </template>

    <div v-if="loading" class="flex items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>

    <div v-else class="provider-editor">
      <CoarNotice v-if="error" variant="error" class="provider-error">
        {{ error }}
      </CoarNotice>

      <CoarTabGroup v-if="showProtocolTabs" v-model="activeTab">
        <CoarTab id="general">
          <span class="tab-label">
            {{ t('admin.loginProviders.tabGeneral', {}, 'Allgemein') }}
            <CoarPopover
              v-if="tabIssue('general').length"
              class="tab-issue-popover"
              mode="hover"
              :offset="8">
              <span
                class="tab-issue"
                role="img"
                :aria-label="tabIssue('general').join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
              <template #content>
                <div class="tab-issue-panel">
                  <h4>{{ t('admin.loginProviders.validation.incomplete', {}, 'Fehlende Angaben') }}</h4>
                  <ul>
                    <li v-for="issue in tabIssue('general')" :key="issue">{{ issue }}</li>
                  </ul>
                </div>
              </template>
            </CoarPopover>
          </span>
        </CoarTab>
        <CoarTab id="connection">
          <span class="tab-label">
            {{ t('admin.loginProviders.tabConnection', {}, 'Verbindung') }}
            <CoarPopover
              v-if="tabIssue('connection').length"
              class="tab-issue-popover"
              mode="hover"
              :offset="8">
              <span
                class="tab-issue"
                role="img"
                :aria-label="tabIssue('connection').join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
              <template #content>
                <div class="tab-issue-panel">
                  <h4>{{ t('admin.loginProviders.validation.incomplete', {}, 'Fehlende Angaben') }}</h4>
                  <ul>
                    <li v-for="issue in tabIssue('connection')" :key="issue">{{ issue }}</li>
                  </ul>
                </div>
              </template>
            </CoarPopover>
          </span>
        </CoarTab>
        <CoarTab v-if="hasAdvancedFields" id="advanced">
          {{ t('admin.loginProviders.tabAdvanced', {}, 'Protokoll & Sicherheit') }}
        </CoarTab>
        <CoarTab v-if="isSaml" id="claim-mapping">{{ t('admin.loginProviders.tabClaimMapping', {}, 'Claim-Mapping') }}</CoarTab>
        <CoarTab id="claims">{{ t('admin.loginProviders.tabUserUpdate', {}, 'User-Update-Script') }}</CoarTab>
        <CoarTab id="linking">{{ t('admin.loginProviders.tabLinking', {}, 'Benutzer & Vertrauen') }}</CoarTab>
      </CoarTabGroup>

      <!-- General tab (always visible — also the only tab for Internal) -->
      <div v-show="!showProtocolTabs || activeTab === 'general'" class="tab-content">
        <div class="provider-form">
          <!-- Flavor is a create-time property of the provider, not a global
               modal action. Keeping it in the form makes the sequence clear:
               choose the provider type, then configure and save the complete
               object. It disappears in Edit mode because it is immutable. -->
          <section v-if="isCreate && !isInternal && flavorOptions.length > 0" class="form-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h3 class="section-divider__title">
                {{ t('admin.loginProviders.section.provider', {}, 'Provider') }}
              </h3>
            </CoarDivider>
            <div class="modal-form-grid">
              <CoarFormField
                class="col-half"
                :label="t('admin.loginProviders.flavor', {}, 'Flavor')"
                :hint="t('admin.loginProviders.flavor.hint', {}, 'Legt Protokoll, Felder und sinnvolle Standardwerte fest.')">
                <CoarSelect
                  v-model="flavorKey"
                  :options="flavorOptions"
                  :aria-label="t('admin.loginProviders.flavor', {}, 'Flavor')"
                />
              </CoarFormField>
              <CoarFormField
                class="col-half provider-enabled-field"
                :label="t('admin.loginProviders.active', {}, 'Aktiv')"
                :hint="t('admin.loginProviders.active.hint', {}, 'Aktive Provider erscheinen sofort auf der Login-Seite. Die Verbindung muss dafür vollständig konfiguriert sein.')"
                layout="inline"
                label-position="after">
                <CoarCheckbox v-model="form.Enabled" :disabled="isBuiltIn" />
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Identität -->
          <section class="form-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h3 class="section-divider__title">
                {{ t('admin.loginProviders.section.identity', {}, 'Identität') }}
              </h3>
            </CoarDivider>
            <div class="modal-form-grid">
              <!-- Display Name (left) + Slug (right). The slug is URL-stable +
                   immutable after create; it's hidden for the built-in Internal
                   provider (fixed slug) and read-only in Edit mode. While the
                   admin hasn't hand-edited the slug, it tracks the Display Name
                   live (normalised: lowercase, non-alphanumerics → hyphens). -->
              <CoarFormField
                class="col-half"
                :label="t('admin.loginProviders.displayName', {}, 'Display Name')"
                :hint="t('admin.loginProviders.displayName.hint', {}, 'Erscheint auf dem Login-Button; setzt den Slug vor.')"
                :error="displayNameError"
                required>
                <CoarTextInput v-model="form.DisplayName" :disabled="isBuiltIn" clearable />
              </CoarFormField>
              <CoarFormField
                v-if="!isInternal"
                class="col-half"
                :label="t('admin.loginProviders.slug', {}, 'Slug')"
                  :hint="slugHint"
                  :error="slugError"
                  :required="isCreate">
                <CoarTextInput
                  :model-value="form.Slug"
                  :disabled="!isCreate"
                  :placeholder="t('admin.loginProviders.slugPlaceholder', {}, 'z. B. acme-entra')"
                  clearable
                  @update:model-value="onSlugInput" />
              </CoarFormField>
              <CoarFormField class="col-full" :label="t('common.description', {}, 'Beschreibung')"
                :hint="t('admin.loginProviders.description.hint', {}, 'Optional note for the internal description of this provider.')">
                <CoarTextInput v-model="form.Description" :disabled="isBuiltIn" clearable :rows="2" />
              </CoarFormField>
            </div>
          </section>

          <!-- Section: Erscheinungsbild — login-button presentation. Hidden for
               the built-in Internal provider (no external button). -->
          <section v-if="!isInternal" class="form-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h3 class="section-divider__title">
                {{ t('admin.loginProviders.section.appearance', {}, 'Erscheinungsbild') }}
              </h3>
            </CoarDivider>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.loginProviders.iconName', {}, 'Button-Icon')"
                :hint="t('admin.loginProviders.iconName.hint', {}, 'Name of a Lucide icon (e.g. microsoft, key, building). See lucide.dev.')">
                <CoarTextInput v-model="form.IconName" :disabled="isBuiltIn" placeholder="microsoft" clearable />
              </CoarFormField>
              <CoarFormField class="col-half" :label="t('admin.loginProviders.buttonColorHex', {}, 'Button-Farbe')"
                :hint="t('admin.loginProviders.buttonColorHex.hint', {}, 'Hex color of the login button (optional, e.g. #0078D4).')">
                <ColorField v-model="form.ButtonColorHex" :disabled="isBuiltIn" placeholder="#0078D4" />
              </CoarFormField>
              <div class="col-full login-button-preview-wrap">
                <span class="preview-label">{{ t('admin.loginProviders.preview', {}, 'Vorschau') }}</span>
                <div
                  class="login-button-preview"
                  :style="loginButtonPreviewStyle">
                  <CoarIcon :name="form.IconName || flavor?.DefaultIconName || 'log-in'" size="s" />
                  <span>{{ form.DisplayName || t('admin.loginProviders.previewFallback', {}, 'Mit Provider anmelden') }}</span>
                </div>
              </div>
            </div>
          </section>
        </div>
      </div>

      <!-- Protocol-specific tabs — OIDC + SAML share the surface; per-flavor
           specifics are gated below. Internal hides the whole block. -->
      <template v-if="showProtocolTabs">
        <!-- Connection tab -->
        <div v-show="activeTab === 'connection'" class="tab-content">
          <div class="provider-form connection-form">
            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.loginProviders.section.connection', {}, 'Provider-Verbindung') }}
                </h3>
              </CoarDivider>
              <FlavorConnectionPanel
                v-if="flavor"
                :schema="flavor.ConfigSchema"
                section="connection"
                :field-errors="connectionFieldErrors"
                v-model="form.FlavorData">
                <template v-if="showOidcConnectionFields">
                  <CoarFormField
                    class="flavor-field"
                    :label="t('admin.loginProviders.clientId', {}, 'Client-ID')"
                    :hint="t('admin.loginProviders.clientId.hint', {}, 'Client-ID aus der App-Registrierung des externen IdP.')"
                    :error="clientIdError">
                    <CoarTextInput
                      v-model="form.ClientId"
                      :disabled="isBuiltIn"
                      placeholder="application-client-id"
                      clearable />
                  </CoarFormField>
                  <CoarFormField
                    class="flavor-field"
                    :label="t('admin.loginProviders.scopes', {}, 'Scopes')"
                    :hint="t('admin.loginProviders.scopes.hint', {}, 'Leer- oder komma-getrennte OIDC-Scopes.')">
                    <CoarTextInput
                      :model-value="form.Scopes.join(' ')"
                      :disabled="isBuiltIn"
                      placeholder="openid profile email"
                      clearable
                      @update:model-value="(v: string) => form.Scopes = parseList(v)" />
                  </CoarFormField>
                </template>
              </FlavorConnectionPanel>
            </section>

            <section v-if="showOidcConnectionFields" class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.loginProviders.section.credentials', {}, 'Client-Zugangsdaten') }}
                </h3>
              </CoarDivider>
              <CoarFormField
                class="secret-field"
                :label="t('admin.loginProviders.clientSecret', {}, 'Client-Secret')"
                :hint="isCreate
                  ? t('admin.loginProviders.secretInitial', {}, 'Wird beim Erstellen verschlüsselt gespeichert und nicht wieder angezeigt.')
                  : provider?.HasClientSecret
                    ? t('admin.loginProviders.secretSet', {}, 'Secret ist konfiguriert. Einen neuen Wert eingeben, um es zu rotieren.')
                    : t('admin.loginProviders.secretMissing', {}, 'Kein Secret gesetzt — vor dem Aktivieren eines konfigurieren.')"
                :error="clientSecretError">
                <div class="secret-input-row">
                  <CoarPasswordInput
                    v-model="newSecret"
                    :disabled="isBuiltIn"
                    clearable
                    placeholder="••••••••" />
                  <CoarButton
                    v-if="!isCreate && provider"
                    :disabled="!newSecret || saving || isBuiltIn"
                    @click="rotateSecret">
                    {{ provider.HasClientSecret
                      ? t('admin.loginProviders.rotateSecret', {}, 'Rotieren')
                      : t('admin.loginProviders.setSecret', {}, 'Setzen') }}
                  </CoarButton>
                </div>
              </CoarFormField>
            </section>

            <section v-if="redirectUri || (isSaml && samlSpMetadataUrl)" class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.loginProviders.section.idpIntegration', {}, 'IdP-Integration') }}
                </h3>
              </CoarDivider>
              <div class="integration-urls">
                <template v-if="isSaml && samlSpMetadataUrl">
                  <CoarFormField :label="t('admin.loginProviders.samlSpMetadataUrl', {}, 'SP-Metadata-URL')"
                    :hint="t('admin.loginProviders.idpReadOnlyHint', {}, 'Schreibgeschützt — beim externen IdP eintragen.')">
                    <div class="copy-field">
                      <CoarTextInput :model-value="samlSpMetadataUrl" readonly />
                      <CoarButton size="s" variant="ghost" icon-start="copy" @click="copyText(samlSpMetadataUrl)">
                        {{ t('common.copy', {}, 'Kopieren') }}
                      </CoarButton>
                    </div>
                  </CoarFormField>
                  <CoarFormField :label="t('admin.loginProviders.samlAcsUrl', {}, 'ACS-URL / Reply-URL')"
                    :hint="t('admin.loginProviders.idpReadOnlyHint', {}, 'Schreibgeschützt — beim externen IdP eintragen.')">
                    <div class="copy-field">
                      <CoarTextInput :model-value="samlAcsUrl" readonly />
                      <CoarButton size="s" variant="ghost" icon-start="copy" @click="copyText(samlAcsUrl)">
                        {{ t('common.copy', {}, 'Kopieren') }}
                      </CoarButton>
                    </div>
                  </CoarFormField>
                </template>
                <CoarFormField v-else-if="redirectUri" :label="t('admin.loginProviders.redirectUri', {}, 'Redirect URI')"
                  :hint="t('admin.loginProviders.idpReadOnlyHint', {}, 'Schreibgeschützt — beim externen IdP eintragen.')">
                  <div class="copy-field">
                    <CoarTextInput :model-value="redirectUri" readonly />
                    <CoarButton size="s" variant="ghost" icon-start="copy" @click="copyRedirect">
                      {{ t('common.copy', {}, 'Kopieren') }}
                    </CoarButton>
                  </div>
                </CoarFormField>
              </div>
            </section>
          </div>
        </div>

        <!-- Protocol and security settings. -->
        <div v-show="activeTab === 'advanced'" class="tab-content">
          <div v-if="flavor" class="provider-form advanced-form">
            <template v-if="isSaml">
              <section class="form-section">
                <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                  <h3 class="section-divider__title">
                    {{ t('admin.loginProviders.section.samlProtection', {}, 'Signaturen & Verschlüsselung') }}
                  </h3>
                </CoarDivider>
                <FlavorConnectionPanel
                  :schema="flavor.ConfigSchema"
                  section="advanced"
                  :include-keys="['WantAssertionsSigned', 'WantResponseSigned', 'SignAuthnRequest', 'WantAssertionsEncrypted']"
                  v-model="form.FlavorData" />
              </section>
              <section class="form-section">
                <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                  <h3 class="section-divider__title">
                    {{ t('admin.loginProviders.section.samlProtocol', {}, 'Protokoll & Metadaten') }}
                  </h3>
                </CoarDivider>
                <FlavorConnectionPanel
                  :schema="flavor.ConfigSchema"
                  section="advanced"
                  :include-keys="['NameIdFormat', 'EntityId', 'MetadataRefreshIntervalSeconds']"
                  v-model="form.FlavorData" />
              </section>
            </template>
            <div v-else class="oidc-protocol-groups">
              <section class="form-section">
                <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                  <h3 class="section-divider__title">
                    {{ t('admin.loginProviders.section.oidcRequest', {}, 'Anmeldeanforderung') }}
                  </h3>
                </CoarDivider>
                <FlavorConnectionPanel
                  :schema="flavor.ConfigSchema"
                  section="advanced"
                  :columns="1"
                  :include-keys="['Prompt', 'UsePkce']"
                  v-model="form.FlavorData" />
              </section>
              <section class="form-section">
                <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                  <h3 class="section-divider__title">
                    {{ t('admin.loginProviders.section.oidcClaimsTokens', {}, 'Claims & Tokens') }}
                  </h3>
                </CoarDivider>
                <FlavorConnectionPanel
                  :schema="flavor.ConfigSchema"
                  section="advanced"
                  :columns="1"
                  :include-keys="['GetClaimsFromUserInfoEndpoint', 'SaveTokens']"
                  v-model="form.FlavorData" />
              </section>
            </div>
          </div>
        </div>

        <!-- Claim-Mapping tab (SAML) — logical claim names → IdP attribute URIs,
             and AuthnContextClassRef → AMR values. Editable so admins can adapt
             when the IdP changes a claim URI. -->
        <div v-show="activeTab === 'claim-mapping'" class="tab-content tab-content--fixed">
          <div class="claim-mapping-layout">
            <nav
              class="mapping-nav"
              :aria-label="t('admin.loginProviders.mappingNavigation', {}, 'Claim-Mapping auswählen')">
              <button
                type="button"
                class="mapping-nav__item"
                :class="{ 'mapping-nav__item--active': activeClaimMap === 'attributes' }"
                :aria-current="activeClaimMap === 'attributes' ? 'page' : undefined"
                @click="activeClaimMap = 'attributes'">
                <span>{{ t('admin.loginProviders.attributeMapShort', {}, 'Attribut-Mapping') }}</span>
                <span class="mapping-nav__count">{{ attributeMapCount }}</span>
              </button>
              <button
                type="button"
                class="mapping-nav__item"
                :class="{ 'mapping-nav__item--active': activeClaimMap === 'amr' }"
                :aria-current="activeClaimMap === 'amr' ? 'page' : undefined"
                @click="activeClaimMap = 'amr'">
                <span>{{ t('admin.loginProviders.amrMappingShort', {}, 'AMR-Mapping') }}</span>
                <span class="mapping-nav__count">{{ amrMappingCount }}</span>
              </button>
            </nav>
            <div class="claim-mapping-panel">
              <ClaimMapEditor
                v-show="activeClaimMap === 'attributes'"
                :model-value="(form.FlavorData.attributeMap as Record<string, string[]>) ?? {}"
                :reload-key="claimReloadKey"
                :key-label="t('admin.loginProviders.claimLogicalName', {}, 'Claim (z. B. email, given_name)')"
                :value-label="t('admin.loginProviders.claimUris', {}, 'SAML-Attribut-URIs (komma-getrennt)')"
                key-placeholder="email"
                value-placeholder="http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress, email"
                :add-label="t('admin.loginProviders.addMapping', {}, 'Mapping hinzufügen')"
                @update:model-value="(v) => form.FlavorData = { ...form.FlavorData, attributeMap: v }" />
              <ClaimMapEditor
                v-show="activeClaimMap === 'amr'"
                :model-value="(form.FlavorData.amrMapping as Record<string, string[]>) ?? {}"
                :reload-key="claimReloadKey"
                :key-label="t('admin.loginProviders.amrClassRef', {}, 'AuthnContextClassRef-URI')"
                :value-label="t('admin.loginProviders.amrValues', {}, 'AMR-Werte (komma-getrennt)')"
                key-placeholder="urn:oasis:names:tc:SAML:2.0:ac:classes:MultiFactor"
                value-placeholder="mfa"
                :add-label="t('admin.loginProviders.addMapping', {}, 'Mapping hinzufügen')"
                @update:model-value="(v) => form.FlavorData = { ...form.FlavorData, amrMapping: v }" />
            </div>
          </div>
        </div>

        <!-- User-update script tab -->
        <div v-show="activeTab === 'claims'" class="tab-content tab-claims">
          <UserUpdateScriptEditor
            v-model="form.UserUpdateScript"
            :login-provider-id="provider?.Id"
            :is-new="isCreate || !provider"
          />
        </div>

        <!-- Linking + policy tab -->
        <div v-show="activeTab === 'linking'" class="tab-content">
          <div class="provider-form policy-form">
            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.loginProviders.section.provisioning', {}, 'Provisionierung') }}
                </h3>
              </CoarDivider>
              <div class="policy-stack">
                <CoarFormField
                  :label="t('admin.loginProviders.autoCreate', {}, 'Benutzer automatisch anlegen (JIT)')"
                  :hint="t('admin.loginProviders.autoCreate.hint', {}, 'Legt unbekannte Benutzer bei der ersten erfolgreichen Anmeldung lokal an.')"
                  layout="inline"
                  label-position="after">
                  <CoarCheckbox v-model="form.AutoCreateUsers" :disabled="isBuiltIn" />
                </CoarFormField>
              </div>
            </section>

            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.loginProviders.section.accountLinking', {}, 'Kontoverknüpfung') }}
                </h3>
              </CoarDivider>
              <div class="policy-stack">
                <CoarFormField
                  :label="t('admin.loginProviders.allowLinking', {}, 'Verknüpfung im Benutzerprofil erlauben')"
                  :hint="t('admin.loginProviders.allowLinking.hint', {}, 'Benutzer dürfen diesen Provider mit ihrem bestehenden Konto verbinden.')"
                  layout="inline"
                  label-position="after">
                  <CoarCheckbox v-model="form.AllowLinking" :disabled="isBuiltIn" />
                </CoarFormField>
                <CoarFormField
                  :label="t('admin.loginProviders.trustForEmailLink', {}, 'Automatisch über E-Mail verknüpfen')"
                  :hint="t('admin.loginProviders.trustForEmailLink.hint', {}, 'Bindet externe Identitäten bei gleicher E-Mail-Adresse automatisch an bestehende Konten. Nur bei vollständig kontrollierten, tenant-eigenen Providern verwenden.')"
                  layout="inline"
                  label-position="after">
                  <CoarCheckbox v-model="form.TrustForEmailLink" :disabled="isBuiltIn" />
                </CoarFormField>
                <CoarNotice v-if="form.TrustForEmailLink" variant="warning" class="policy-warning">
                  {{ t('admin.loginProviders.trustForEmailLink.warning', {}, 'E-Mail-Auto-Verknüpfung ist sicherheitskritisch und darf nur für tenant-eigene Provider aktiviert werden.') }}
                </CoarNotice>
                <CoarFormField
                  :label="t('admin.loginProviders.allowedEmailDomains', {}, 'Erlaubte E-Mail-Domänen')"
                  :hint="t('admin.loginProviders.allowedEmailDomains.hint', {}, 'Komma- oder leerzeichengetrennt. Leer bedeutet keine Einschränkung.')">
                  <CoarTextInput
                    :model-value="form.AllowedEmailDomains.join(', ')"
                    :disabled="isBuiltIn"
                    placeholder="acme.com, contoso.com"
                    clearable
                    @update:model-value="(v: string) => form.AllowedEmailDomains = parseList(v)" />
                </CoarFormField>
              </div>
            </section>

            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.loginProviders.section.trust', {}, 'Profil & Autorisierung') }}
                </h3>
              </CoarDivider>
              <div class="policy-stack">
                <CoarFormField
                  :label="t('admin.loginProviders.trustForAuthorization', {}, 'Für Autorisierung vertrauen')"
                  :hint="t('admin.loginProviders.trustForAuthorization.hint', {}, 'Darf sitzungsbezogene Mitgliedschaften in extern steuerbaren Gruppen ableiten; niemals realm:admin.')"
                  layout="inline"
                  label-position="after">
                  <CoarCheckbox v-model="form.TrustForAuthorization" :disabled="isBuiltIn" />
                </CoarFormField>
                <CoarFormField
                  :label="t('admin.loginProviders.authoritativeForProfile', {}, 'Profil-autoritativ')"
                  :hint="t('admin.loginProviders.authoritativeForProfile.hint', {}, 'Darf Vorname, Nachname, E-Mail und Kürzel bei jeder Anmeldung aktualisieren.')"
                  layout="inline"
                  label-position="after">
                  <CoarCheckbox v-model="form.AuthoritativeForProfile" :disabled="isBuiltIn" />
                </CoarFormField>
              </div>
            </section>

            <section class="form-section">
              <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
                <h3 class="section-divider__title">
                  {{ t('admin.loginProviders.section.diagnostics', {}, 'Diagnose') }}
                </h3>
              </CoarDivider>
              <div class="policy-stack">
                <CoarFormField
                  :label="t('admin.loginProviders.storeRawClaims', {}, 'Roh-Claims speichern')"
                  :hint="t('admin.loginProviders.storeRawClaims.hint', {}, 'Speichert den vom IdP gelieferten Claim-Snapshot pro Anmeldung für Diagnosezwecke.')"
                  layout="inline"
                  label-position="after">
                  <CoarCheckbox v-model="form.StoreRawClaims" :disabled="isBuiltIn" />
                </CoarFormField>
                <CoarFormField
                  v-if="form.StoreRawClaims"
                  :label="t('admin.loginProviders.rawRetentionDays', {}, 'Aufbewahrung in Tagen')"
                  :hint="t('admin.loginProviders.rawRetentionDays.hint', {}, 'Leer bedeutet unbegrenzt.')">
                  <CoarNumberInput
                    v-model="form.RawClaimsRetentionDays"
                    :disabled="isBuiltIn"
                    :min="1"
                    :max="3650"
                    :step="30"
                    stepper-buttons="both"
                    clearable />
                </CoarFormField>
              </div>
            </section>
          </div>
        </div>
      </template>
    </div>
  </ModalLayout>
</template>

<style scoped>
.provider-editor {
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
  flex-direction: column;
  gap: 0.75rem;
}

.provider-error {
  flex-shrink: 0;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.tab-issue {
  display: inline-flex;
  align-items: center;
  color: var(--coar-text-semantic-warning, #a15c00);
  cursor: help;
}

.tab-issue-popover {
  display: inline-flex;
  align-items: center;
}

.tab-issue-panel {
  min-width: 15rem;
  max-width: 24rem;
  padding: 0.75rem 0.875rem;
}

.tab-issue-panel h4 {
  margin: 0 0 0.5rem;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.tab-issue-panel ul {
  display: grid;
  gap: 0.35rem;
  margin: 0;
  padding-left: 1.125rem;
  color: var(--coar-text-neutral-secondary, #4b5563);
  font-size: 0.8125rem;
  line-height: 1.4;
}

.tab-content {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 8px 4px;
  overflow-y: auto;
}

.tab-content.tab-claims {
  overflow: hidden;
}

.tab-content--fixed {
  overflow: hidden;
}

.provider-form {
  width: 100%;
  max-width: 72rem;
  min-width: 0;
}

.form-section + .form-section {
  margin-top: 1.5rem;
}

.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.provider-enabled-field {
  align-self: center;
  transform: translateY(0.625rem);
}

.login-button-preview-wrap {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.preview-label {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.login-button-preview {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 16rem;
  min-height: 2.5rem;
  padding: 0.5rem 0.875rem;
  border: 1px solid var(--coar-border-neutral-subtle, #d4d8e1);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-secondary, #f3f4f6);
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.connection-form,
.advanced-form,
.policy-form {
  padding-bottom: 0.5rem;
}

.policy-form {
  max-width: 44rem;
}

.oidc-protocol-groups {
  display: flex;
  flex-direction: column;
  width: 100%;
  max-width: 44rem;
}

.oidc-protocol-groups .form-section + .form-section {
  margin-top: 1.5rem;
}

.flavor-field {
  min-width: 0;
}

.secret-field {
  max-width: 44rem;
}

.secret-input-row,
.copy-field {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.secret-input-row > :first-child,
.copy-field > :first-child {
  flex: 1;
  min-width: 0;
}

.integration-urls {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.875rem 1rem;
}

.policy-stack {
  display: flex;
  flex-direction: column;
  gap: 0.875rem;
}

.policy-warning {
  margin: 0;
}

.claim-mapping-layout {
  display: grid;
  flex: 1;
  grid-template-columns: minmax(12rem, 14rem) minmax(0, 1fr);
  gap: 1rem;
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

.mapping-nav {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  padding-right: 1rem;
  border-right: 1px solid var(--coar-border-neutral, #e5e7eb);
}

.mapping-nav__item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
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

.mapping-nav__item:hover {
  background: var(--coar-surface-neutral-hover, #f3f4f6);
}

.mapping-nav__item--active {
  background: var(--coar-surface-primary-subtle, #eff6ff);
  color: var(--coar-text-primary, #0369a1);
  font-weight: 600;
}

.mapping-nav__count {
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

.mapping-nav__item--active .mapping-nav__count {
  background: var(--coar-surface-primary-muted, #dbeafe);
  color: inherit;
}

.claim-mapping-panel {
  min-width: 0;
  min-height: 0;
  overflow: hidden;
}

@media (max-width: 900px) {
  .integration-urls,
  .claim-mapping-layout {
    grid-template-columns: 1fr;
  }

  .mapping-nav {
    flex-direction: row;
    overflow-x: auto;
    padding-right: 0;
    padding-bottom: 0.75rem;
    border-right: 0;
    border-bottom: 1px solid var(--coar-border-neutral, #e5e7eb);
  }

  .mapping-nav__item {
    width: auto;
    white-space: nowrap;
  }
}
</style>
