<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import {
  CoarTextInput, CoarFormField, CoarCheckbox, CoarTabGroup, CoarTab,
  CoarButton, CoarIcon, CoarNote, CoarSelect,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation } from '@cocoar/vue-fragment-parser'
import ModalLayout from '@/components/ModalLayout.vue'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import type { FlavorConfigFieldDto, FlavorDto, LoginProviderDto } from '@/models/loginProvider'
import UserUpdateScriptEditor from './UserUpdateScriptEditor.vue'
import FlavorConnectionPanel from './panels/FlavorConnectionPanel.vue'

const { t } = useI18n()
const store = useLoginProviderStore()
const { navigateToModal } = useFragmentNavigation()

const props = defineProps<{ id: string; close: (result?: unknown) => void }>()
const isCreate = computed(() => props.id === 'create')

const activeTab = ref<'general' | 'connection' | 'claims' | 'linking'>('general')
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
  AllowedEmailDomains: string[]
  IconName: string
  ButtonColorHex: string
  FlavorData: Record<string, unknown>
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
    AllowedEmailDomains: [],
    IconName: '',
    ButtonColorHex: '',
    FlavorData: {},
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

// Auto-fill the slug from DisplayName while the admin hasn't typed one (Add mode
// only — the slug is immutable after create).
function suggestSlug() {
  if (!isCreate.value || form.value.Slug.trim()) return
  form.value.Slug = slugify(form.value.DisplayName)
}

const modalTitle = computed(() => {
  if (isCreate.value) return t('admin.loginProviders.createTitle', {}, 'Login-Provider hinzufügen')
  return provider.value?.DisplayName || ''
})

const redirectUri = computed(() => provider.value?.RedirectUri ?? '')
const samlSpMetadataUrl = computed(() => provider.value?.SamlSpMetadataUrl ?? '')
const samlAcsUrl = computed(() => provider.value?.SamlAcsUrl ?? '')

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
  form.value.FlavorData = {} // ConfigSchema differs per flavor — start clean.
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
      error.value = t('admin.loginProviders.notFound', {}, 'Nicht gefunden')
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
      AllowedEmailDomains: existing.AllowedEmailDomains ? [...existing.AllowedEmailDomains] : [],
      IconName: existing.IconName ?? '',
      ButtonColorHex: existing.ButtonColorHex ?? '',
      FlavorData: normalizeFlavorData(existing.FlavorData, loadedFlavor?.ConfigSchema),
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
})

async function save() {
  if (!form.value.DisplayName.trim()) {
    error.value = t('admin.loginProviders.nameRequired', {}, 'Name ist erforderlich')
    return
  }
  // Slug is required + format-checked at create only (immutable afterwards).
  if (isCreate.value && !SLUG_RE.test(form.value.Slug.trim())) {
    error.value = t('admin.loginProviders.slugInvalid', {},
      'Slug muss 3-64 Zeichen lang sein: Kleinbuchstaben, Ziffern, Bindestriche; Beginn mit Buchstabe, Ende alphanumerisch.')
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
    error.value = t('admin.loginProviders.flavorRequired', {}, 'Flavor auswählen')
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
    const missing = (flavor.value.ConfigSchema ?? [])
      .filter((f) => f.Required)
      .filter((f) => {
        const v = form.value.FlavorData[f.Key]
        return v === undefined || v === null || (typeof v === 'string' && v.trim() === '')
      })
    if (missing.length > 0) {
      activeTab.value = 'connection'
      const names = missing.map((f) => f.Label).join(', ')
      error.value = t(
        'admin.loginProviders.requiredFieldsMissing', {},
        `Pflichtfelder fehlen im Verbindung-Tab: ${names}`
      )
      return
    }
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
    // Security default — admin enables explicitly after smoke-test + (for OIDC)
    // setting an initial secret via the field below.
    Enabled: false,
    ClientId: form.value.ClientId.trim() || null,
    Scopes: form.value.Scopes,
    UserUpdateScript: form.value.UserUpdateScript,
    StoreRawClaims: form.value.StoreRawClaims,
    RawClaimsRetentionDays: form.value.RawClaimsRetentionDays,
    AutoCreateUsers: form.value.AutoCreateUsers,
    AllowLinking: form.value.AllowLinking,
    TrustForEmailLink: form.value.TrustForEmailLink,
    AllowedEmailDomains: form.value.AllowedEmailDomains.length > 0 ? form.value.AllowedEmailDomains : null,
    IconName: form.value.IconName || null,
    ButtonColorHex: form.value.ButtonColorHex || null,
  })

  // OIDC initial secret: secret stays out of Create for audit-trail reasons.
  // If the admin entered one in the picker, fire RotateClientSecret right after
  // Create — same surface, separate audit event. Best-effort: a failed secret
  // set does not bin the new provider; admin can retry from the now-Edit modal.
  if (newSecret.value && flavor.value.Type === 'Oidc') {
    try { await store.rotateSecret(created.Id, newSecret.value) }
    catch (e: any) {
      // Surface the warning but let the modal transition to Edit mode so the
      // admin sees their saved provider and can retry the secret.
      error.value = t('admin.loginProviders.secretRotationFailed', {},
        'Provider angelegt, aber Secret konnte nicht gesetzt werden — bitte unter "Verbindung" erneut versuchen.')
    }
    newSecret.value = ''
  }

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
    AllowedEmailDomains: form.value.AllowedEmailDomains.length > 0 ? form.value.AllowedEmailDomains : null,
    IconName: form.value.IconName || null,
    ButtonColorHex: form.value.ButtonColorHex || null,
    FlavorData: form.value.FlavorData,
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

async function toggleEnabled() {
  if (!provider.value || isBuiltIn.value) return
  saving.value = true
  error.value = null
  try {
    const updated = provider.value.Enabled
      ? await store.disable(provider.value.Id)
      : await store.enable(provider.value.Id)
    provider.value = updated
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
    ? t('common.create', {}, 'Erstellen')
    : t('common.save', {}, 'Speichern'),
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
  >
    <!--
      Header-actions slot: flavor picker lives next to the title so switching
      type in Add mode visibly morphs the modal. Disabled in Edit (Type/Flavor
      are immutable after Create) so the admin can still see *which* flavor
      the existing provider runs.
    -->
    <template #header-actions>
      <CoarSelect
        v-if="!isInternal && flavorOptions.length > 0"
        v-model="flavorKey"
        :options="flavorOptions"
        :disabled="!isCreate"
        :aria-label="t('admin.loginProviders.flavor', {}, 'Flavor')"
      />
    </template>

    <div v-if="loading" class="flex items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>

    <div v-else class="flex flex-col flex-1 min-h-0 gap-3">
      <!-- Built-in banner — hard-block on edits via UI; backend rejects too. -->
      <CoarNote v-if="isBuiltIn" variant="info">
        {{ t('admin.loginProviders.builtIn.banner', {}, 'Dies ist der eingebaute interne Login-Provider — die Konfiguration wird vom System verwaltet und kann hier nicht geändert werden.') }}
      </CoarNote>

      <!-- Header row: type/flavor badge, enabled-toggle, error -->
      <div class="flex items-center gap-3 flex-wrap">
        <div class="badge">
          <CoarIcon
            :name="(provider?.IconName ?? flavor?.DefaultIconName) ?? (isInternal ? 'lock' : 'key-round')"
            size="s"
          />
          <span>
            {{ isInternal
              ? t('admin.loginProviders.type.values.internal', {}, 'Intern')
              : (flavor?.DisplayName ?? provider?.Flavor) }}
          </span>
        </div>
        <!-- Enabled toggle only on existing providers — in Add mode the
             security default is Enabled=false (admin enables explicitly after
             smoke-test). -->
        <CoarButton
          v-if="provider && !isBuiltIn"
          size="s"
          :variant="provider.Enabled ? 'primary' : 'ghost'"
          :icon-start="provider.Enabled ? 'circle-check' : 'circle-off'"
          :disabled="saving"
          @click="toggleEnabled"
        >
          {{ provider.Enabled
            ? t('admin.loginProviders.enabled', {}, 'Aktiviert')
            : t('admin.loginProviders.disabled', {}, 'Deaktiviert') }}
        </CoarButton>
        <div v-if="error" class="error-banner flex-1">{{ error }}</div>
      </div>

      <CoarTabGroup v-if="showProtocolTabs" v-model="activeTab">
        <CoarTab id="general">{{ t('admin.loginProviders.tabGeneral', {}, 'Allgemein') }}</CoarTab>
        <CoarTab id="connection">{{ t('admin.loginProviders.tabConnection', {}, 'Verbindung') }}</CoarTab>
        <CoarTab id="claims">{{ t('admin.loginProviders.tabUserUpdate', {}, 'User-Update-Script') }}</CoarTab>
        <CoarTab id="linking">{{ t('admin.loginProviders.tabLinking', {}, 'Verknüpfung & Richtlinien') }}</CoarTab>
      </CoarTabGroup>

      <!-- General tab (always visible — also the only tab for Internal) -->
      <div v-show="!showProtocolTabs || activeTab === 'general'" class="tab-content">
        <CoarFormField :label="t('admin.loginProviders.displayName', {}, 'Display Name')">
          <CoarTextInput v-model="form.DisplayName" :disabled="isBuiltIn" clearable @blur="suggestSlug" />
        </CoarFormField>
        <!-- Slug: URL-stable identifier, immutable after create. Hidden for the
             built-in Internal provider (its slug is fixed). In Edit mode it is
             shown read-only so the admin can see / copy it. -->
        <CoarFormField
          v-if="!isInternal"
          :label="t('admin.loginProviders.slug', {}, 'Slug')"
          :required="isCreate"
          :hint="isCreate
            ? t('admin.loginProviders.slugHintCreate', {}, 'Erscheint in den Provider-URLs (z. B. /signin-oidc/<slug>). Nach dem Anlegen nicht mehr änderbar.')
            : t('admin.loginProviders.slugHintEdit', {}, 'Nicht änderbar — ein anderer Slug bedeutet löschen + neu anlegen.')">
          <CoarTextInput
            v-model="form.Slug"
            :disabled="!isCreate"
            :placeholder="t('admin.loginProviders.slugPlaceholder', {}, 'z. B. acme-entra')"
            clearable />
        </CoarFormField>
        <CoarFormField :label="t('common.description', {}, 'Beschreibung')">
          <CoarTextInput v-model="form.Description" :disabled="isBuiltIn" clearable />
        </CoarFormField>
        <template v-if="!isInternal && !isCreate">
          <!-- URLs: only meaningful once the provider exists — in Add mode they
               appear after Save when the modal transitions to Edit mode. -->
          <template v-if="isSaml">
            <CoarFormField :label="t('admin.loginProviders.samlSpMetadataUrl', {}, 'SP-Metadata-URL (in die IdP-Konfiguration eintragen)')">
              <div class="flex gap-2 items-center">
                <CoarTextInput :model-value="samlSpMetadataUrl" readonly class="flex-1" />
                <CoarButton size="s" variant="ghost" icon-start="copy" @click="copyText(samlSpMetadataUrl)">
                  {{ t('common.copy', {}, 'Kopieren') }}
                </CoarButton>
              </div>
            </CoarFormField>
            <CoarFormField :label="t('admin.loginProviders.samlAcsUrl', {}, 'ACS-URL / Reply-URL (in die IdP-Konfiguration eintragen)')">
              <div class="flex gap-2 items-center">
                <CoarTextInput :model-value="samlAcsUrl" readonly class="flex-1" />
                <CoarButton size="s" variant="ghost" icon-start="copy" @click="copyText(samlAcsUrl)">
                  {{ t('common.copy', {}, 'Kopieren') }}
                </CoarButton>
              </div>
            </CoarFormField>
          </template>
          <CoarFormField v-else :label="t('admin.loginProviders.redirectUri', {}, 'Redirect URI (in die IdP-App-Registrierung eintragen)')">
            <div class="flex gap-2 items-center">
              <CoarTextInput :model-value="redirectUri" readonly class="flex-1" />
              <CoarButton size="s" variant="ghost" icon-start="copy" @click="copyRedirect">
                {{ t('common.copy', {}, 'Kopieren') }}
              </CoarButton>
            </div>
          </CoarFormField>
        </template>
        <template v-if="!isInternal">
          <CoarFormField :label="t('admin.loginProviders.iconName', {}, 'Button-Icon (Lucide-Name)')">
            <CoarTextInput v-model="form.IconName" :disabled="isBuiltIn" placeholder="microsoft" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.loginProviders.buttonColorHex', {}, 'Button-Farbe (Hex, optional)')">
            <CoarTextInput v-model="form.ButtonColorHex" :disabled="isBuiltIn" placeholder="#0078D4" clearable />
          </CoarFormField>
        </template>
      </div>

      <!-- Protocol-specific tabs — OIDC + SAML share the surface; per-flavor
           specifics are gated below. Internal hides the whole block. -->
      <template v-if="showProtocolTabs">
        <!-- Connection tab -->
        <div v-show="activeTab === 'connection'" class="tab-content">
          <FlavorConnectionPanel
            v-if="flavor"
            :schema="flavor.ConfigSchema"
            v-model="form.FlavorData"
          />
          <template v-if="showOidcConnectionFields">
            <CoarFormField :label="t('admin.loginProviders.clientId', {}, 'Client ID')">
              <CoarTextInput v-model="form.ClientId" :disabled="isBuiltIn" placeholder="application-client-id" clearable />
            </CoarFormField>
            <CoarFormField :label="t('admin.loginProviders.scopes', {}, 'Scopes (leer- oder komma-getrennt)')">
              <CoarTextInput
                :model-value="form.Scopes.join(' ')"
                :disabled="isBuiltIn"
                placeholder="openid profile email"
                clearable
                @update:model-value="(v: string) => form.Scopes = parseList(v)"
              />
            </CoarFormField>

            <div class="secret-section">
              <div class="section-heading">{{ t('admin.loginProviders.clientSecret', {}, 'Client-Secret') }}</div>
              <!-- Add mode: the secret is part of the initial submit. We fire
                   RotateClientSecret right after Create so the audit event
                   shape stays uniform. Edit mode: same rotation surface. -->
              <template v-if="isCreate">
                <div class="text-sm secret-status mb-2">
                  <CoarIcon name="shield-alert" size="s" />
                  <span>{{ t('admin.loginProviders.secretInitial', {}, 'Initiales Secret (optional; kann später unter Verbindung gesetzt werden).') }}</span>
                </div>
                <CoarTextInput v-model="newSecret" type="password" clearable placeholder="••••••••" />
              </template>
              <template v-else-if="provider">
                <div class="text-sm secret-status mb-2">
                  <CoarIcon :name="provider.HasClientSecret ? 'shield-check' : 'shield-alert'" size="s" />
                  <span v-if="provider.HasClientSecret">{{ t('admin.loginProviders.secretSet', {}, 'Secret ist konfiguriert. Neuen Wert eingeben, um zu rotieren.') }}</span>
                  <span v-else>{{ t('admin.loginProviders.secretMissing', {}, 'Kein Secret gesetzt — setze eines, bevor du den Provider aktivierst.') }}</span>
                </div>
                <div class="flex gap-2">
                  <CoarTextInput v-model="newSecret" :disabled="isBuiltIn" type="password" class="flex-1" clearable placeholder="••••••••" />
                  <CoarButton :disabled="!newSecret || saving || isBuiltIn" @click="rotateSecret">
                    {{ provider.HasClientSecret
                      ? t('admin.loginProviders.rotateSecret', {}, 'Rotieren')
                      : t('admin.loginProviders.setSecret', {}, 'Setzen') }}
                  </CoarButton>
                </div>
              </template>
            </div>
          </template>
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
          <CoarCheckbox v-model="form.AutoCreateUsers" :disabled="isBuiltIn"
            :label="t('admin.loginProviders.autoCreate', {}, 'Neue User beim ersten Login automatisch anlegen (JIT)')" />
          <CoarCheckbox v-model="form.AllowLinking" :disabled="isBuiltIn"
            :label="t('admin.loginProviders.allowLinking', {}, 'User dürfen diesen Provider im Profil verknüpfen')" />
          <CoarCheckbox v-model="form.TrustForEmailLink" :disabled="isBuiltIn"
            :label="t('admin.loginProviders.trustForEmailLink', {}, 'Email-basierte Auto-Verknüpfung — bei gleicher Email an bestehenden lokalen User binden (GEFÄHRLICH: nur bei tenant-eigenen Providern)')" />

          <CoarFormField :label="t('admin.loginProviders.allowedEmailDomains', {}, 'Erlaubte Email-Domänen (komma-getrennt, leer = kein Filter)')">
            <CoarTextInput
              :model-value="form.AllowedEmailDomains.join(', ')"
              :disabled="isBuiltIn"
              placeholder="acme.com, contoso.com"
              clearable
              @update:model-value="(v: string) => form.AllowedEmailDomains = parseList(v)"
            />
          </CoarFormField>

          <div class="divider"></div>

          <CoarCheckbox v-model="form.StoreRawClaims" :disabled="isBuiltIn"
            :label="t('admin.loginProviders.storeRawClaims', {}, 'Roh-Claim-Snapshots pro Login speichern (für Debugging)')" />
          <CoarFormField v-if="form.StoreRawClaims"
            :label="t('admin.loginProviders.rawRetentionDays', {}, 'Aufbewahrung der Rohclaims (Tage, leer = unbegrenzt)')">
            <CoarTextInput
              :model-value="form.RawClaimsRetentionDays?.toString() ?? ''"
              :disabled="isBuiltIn"
              type="number"
              clearable
              @update:model-value="(v: string) => form.RawClaimsRetentionDays = v ? Number(v) : null"
            />
          </CoarFormField>
        </div>
      </template>
    </div>
  </ModalLayout>
</template>

<style scoped>
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
.section-heading {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
  border-bottom: 1px solid #d1d5db;
  padding-bottom: 4px;
  margin: 16px 0 8px;
}
.secret-section {
  margin-top: 12px;
}
.secret-status {
  display: flex;
  align-items: center;
  gap: 6px;
  color: #6b7280;
}
.badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  background: var(--coar-background-neutral-secondary, #f3f4f6);
  border-radius: 999px;
  font-size: 0.85rem;
}
.error-banner {
  font-size: 0.85rem;
  color: #b91c1c;
  background: #fef2f2;
  border: 1px solid #fecaca;
  padding: 6px 10px;
  border-radius: 4px;
}
.divider {
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  margin: 12px 0;
}
</style>
