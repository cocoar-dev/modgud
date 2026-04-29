<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarSelect,
  CoarCheckbox,
  CoarButton,
  CoarTabGroup,
  CoarTab,
  CoarNote,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useOAuthClientStore } from '@/stores/oauthClient.store'
import type { OAuthClientDto, CreateOAuthClientDto, UpdateOAuthClientDto } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthClientStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)
const activeTab = ref<'general' | 'urls' | 'lifetimes'>('general')

// Cleartext secret returned once at creation / regeneration — surfaced for copy.
const newSecret = ref<string | null>(null)

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

interface FormState {
  ClientId: string
  DisplayName: string
  ClientType: string
  ConsentType: string
  ClientSecret: string
  Enabled: boolean
  RedirectUris: string  // newline-separated in form
  PostLogoutRedirectUris: string
  AllowedGrantTypes: string  // comma-separated
  AllowedCorsOrigins: string
  RequireClientSecret: boolean
  RequireConsent: boolean
  AllowRememberConsent: boolean
  AllowAccessTokensViaBrowser: boolean
  EnableLocalLogin: boolean
  IdentityTokenLifetime: string
  AccessTokenLifetime: string
  AuthorizationCodeLifetime: string
  AbsoluteRefreshTokenLifetime: string
  SlidingRefreshTokenLifetime: string
}

function emptyForm(): FormState {
  return {
    ClientId: '',
    DisplayName: '',
    ClientType: 'confidential',
    ConsentType: 'implicit',
    ClientSecret: '',
    Enabled: true,
    RedirectUris: '',
    PostLogoutRedirectUris: '',
    AllowedGrantTypes: '',
    AllowedCorsOrigins: '',
    RequireClientSecret: true,
    RequireConsent: false,
    AllowRememberConsent: true,
    AllowAccessTokensViaBrowser: false,
    EnableLocalLogin: true,
    IdentityTokenLifetime: '',
    AccessTokenLifetime: '',
    AuthorizationCodeLifetime: '',
    AbsoluteRefreshTokenLifetime: '',
    SlidingRefreshTokenLifetime: '',
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
    RedirectUris: (dto.RedirectUris ?? []).join('\n'),
    PostLogoutRedirectUris: (dto.PostLogoutRedirectUris ?? []).join('\n'),
    AllowedGrantTypes: (dto.AllowedGrantTypes ?? []).join(', '),
    AllowedCorsOrigins: (dto.AllowedCorsOrigins ?? []).join('\n'),
    RequireClientSecret: dto.RequireClientSecret,
    RequireConsent: dto.RequireConsent,
    AllowRememberConsent: dto.AllowRememberConsent,
    AllowAccessTokensViaBrowser: dto.AllowAccessTokensViaBrowser,
    EnableLocalLogin: dto.EnableLocalLogin,
    IdentityTokenLifetime: dto.IdentityTokenLifetime?.toString() ?? '',
    AccessTokenLifetime: dto.AccessTokenLifetime?.toString() ?? '',
    AuthorizationCodeLifetime: dto.AuthorizationCodeLifetime?.toString() ?? '',
    AbsoluteRefreshTokenLifetime: dto.AbsoluteRefreshTokenLifetime?.toString() ?? '',
    SlidingRefreshTokenLifetime: dto.SlidingRefreshTokenLifetime?.toString() ?? '',
  }
}

function splitLines(input: string): string[] {
  return input.split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)
}
function splitCsv(input: string): string[] {
  return input.split(',').map((s) => s.trim()).filter(Boolean)
}
function parseInt(input: string): number | null {
  const trimmed = input.trim()
  if (!trimmed) return null
  const n = Number.parseInt(trimmed, 10)
  return Number.isFinite(n) ? n : null
}

const modalTitle = computed(() => {
  if (isCreate.value) return t('admin.oauthClients.createTitle', {}, 'OAuth-Client erstellen')
  return form.value.DisplayName || form.value.ClientId
})

const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.ClientId)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Erstellen') : t('common.save', {}, 'Speichern'),
  disabled: !form.value.ClientId.trim() || loading.value,
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  if (isCreate.value) return
  loading.value = true
  try {
    const dto = await store.loadOne(props.id)
    if (!dto) {
      error.value = t('admin.oauthClients.loadFailed', {}, 'Client konnte nicht geladen werden.')
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
        // Keep the modal open so the admin can copy the cleartext secret.
        original.value = created.Client
        form.value = fromDto(created.Client)
      } else {
        props.close()
      }
    } else {
      await store.update(props.id, buildUpdateDto())
      props.close()
    }
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
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
    RequireClientSecret: form.value.RequireClientSecret,
    RequireConsent: form.value.RequireConsent,
    RedirectUris: splitLines(form.value.RedirectUris),
    PostLogoutRedirectUris: splitLines(form.value.PostLogoutRedirectUris),
    AllowedGrantTypes: splitCsv(form.value.AllowedGrantTypes),
    AllowedCorsOrigins: splitLines(form.value.AllowedCorsOrigins),
  }
  const secret = form.value.ClientSecret.trim()
  if (secret) dto.ClientSecret = secret
  return dto
}

function buildUpdateDto(): UpdateOAuthClientDto {
  return {
    DisplayName: form.value.DisplayName.trim() || null,
    ConsentType: form.value.ConsentType,
    Enabled: form.value.Enabled,
    RedirectUris: splitLines(form.value.RedirectUris),
    PostLogoutRedirectUris: splitLines(form.value.PostLogoutRedirectUris),
    AllowedGrantTypes: splitCsv(form.value.AllowedGrantTypes),
    AllowedCorsOrigins: splitLines(form.value.AllowedCorsOrigins),
    RequireClientSecret: form.value.RequireClientSecret,
    RequireConsent: form.value.RequireConsent,
    AllowRememberConsent: form.value.AllowRememberConsent,
    AllowAccessTokensViaBrowser: form.value.AllowAccessTokensViaBrowser,
    EnableLocalLogin: form.value.EnableLocalLogin,
    IdentityTokenLifetime: parseInt(form.value.IdentityTokenLifetime),
    AccessTokenLifetime: parseInt(form.value.AccessTokenLifetime),
    AuthorizationCodeLifetime: parseInt(form.value.AuthorizationCodeLifetime),
    AbsoluteRefreshTokenLifetime: parseInt(form.value.AbsoluteRefreshTokenLifetime),
    SlidingRefreshTokenLifetime: parseInt(form.value.SlidingRefreshTokenLifetime),
  }
}

async function regenerateSecret() {
  if (isCreate.value) return
  if (!confirm(t('admin.oauthClients.confirmRegen', {}, 'Wirklich neu generieren? Das alte Secret wird sofort ungültig.'))) return
  loading.value = true
  try {
    const res = await store.regenerateSecret(props.id)
    newSecret.value = res.ClientSecret
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
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
    :footer-button="footerButton" width="48rem">
    <div v-if="loading && !original && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1">
      <CoarTabGroup v-if="!isCreate" v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.oauthClients.tabs.general', {}, 'Allgemein') }}</CoarTab>
        <CoarTab id="urls">{{ t('admin.oauthClients.tabs.urls', {}, 'URLs & Grants') }}</CoarTab>
        <CoarTab id="lifetimes">{{ t('admin.oauthClients.tabs.lifetimes', {}, 'Token-Laufzeiten') }}</CoarTab>
      </CoarTabGroup>

      <!-- New-secret notice — shown once after create or regenerate -->
      <CoarNote v-if="newSecret" variant="warning" class="mb-3">
        <div class="flex flex-col gap-2">
          <div class="font-medium">{{ t('admin.oauthClients.secretOnce', {}, 'Bitte Client Secret jetzt kopieren — es wird nicht wieder angezeigt.') }}</div>
          <div class="flex items-center gap-2">
            <code class="flex-1 break-all rounded bg-white/40 px-2 py-1 text-xs">{{ newSecret }}</code>
            <CoarButton size="s" variant="secondary" icon-start="copy" @click="copySecret">
              {{ t('common.copy', {}, 'Kopieren') }}
            </CoarButton>
          </div>
        </div>
      </CoarNote>

      <!-- General -->
      <div v-show="isCreate || activeTab === 'general'" class="tab-content">
        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.oauthClients.clientId', {}, 'Client ID')">
            <CoarTextInput v-model="form.ClientId" :disabled="!isCreate" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.displayName', {}, 'Display Name')">
            <CoarTextInput v-model="form.DisplayName" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.type', {}, 'Client-Typ')">
            <CoarSelect v-model="form.ClientType" :options="clientTypeOptions" :disabled="!isCreate" />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.consentType', {}, 'Consent-Typ')">
            <CoarSelect v-model="form.ConsentType" :options="consentTypeOptions" />
          </CoarFormField>
          <CoarFormField v-if="isCreate" :label="t('admin.oauthClients.clientSecret', {}, 'Client Secret (leer = generieren)')">
            <CoarTextInput v-model="form.ClientSecret" type="password" clearable />
          </CoarFormField>
        </div>
        <div class="mt-3 flex flex-wrap gap-x-6 gap-y-2">
          <CoarCheckbox v-model="form.Enabled" :label="t('admin.oauthClients.enabled', {}, 'Aktiv')" />
          <CoarCheckbox v-model="form.RequireClientSecret" :label="t('admin.oauthClients.requireSecret', {}, 'Secret erforderlich')" />
          <CoarCheckbox v-model="form.RequireConsent" :label="t('admin.oauthClients.requireConsent', {}, 'Zustimmung erforderlich')" />
          <CoarCheckbox v-model="form.AllowRememberConsent" :label="t('admin.oauthClients.rememberConsent', {}, 'Zustimmung speichern')" />
          <CoarCheckbox v-model="form.AllowAccessTokensViaBrowser" :label="t('admin.oauthClients.tokensInBrowser', {}, 'Token im Browser erlaubt')" />
          <CoarCheckbox v-model="form.EnableLocalLogin" :label="t('admin.oauthClients.localLogin', {}, 'Lokaler Login erlaubt')" />
        </div>
        <div v-if="!isCreate" class="mt-4">
          <CoarButton size="s" variant="secondary" icon-start="rotate-ccw" :loading="loading" @click="regenerateSecret">
            {{ t('admin.oauthClients.regenerate', {}, 'Client Secret neu generieren') }}
          </CoarButton>
        </div>
      </div>

      <!-- URLs / Grants -->
      <div v-show="!isCreate && activeTab === 'urls'" class="tab-content">
        <CoarFormField :label="t('admin.oauthClients.redirectUris', {}, 'Redirect-URIs (eine pro Zeile)')">
          <textarea v-model="form.RedirectUris" rows="4" class="textarea" />
        </CoarFormField>
        <CoarFormField :label="t('admin.oauthClients.postLogoutRedirectUris', {}, 'Post-Logout Redirect-URIs (eine pro Zeile)')">
          <textarea v-model="form.PostLogoutRedirectUris" rows="3" class="textarea" />
        </CoarFormField>
        <CoarFormField :label="t('admin.oauthClients.grantTypes', {}, 'Allowed Grant Types (kommagetrennt)')">
          <CoarTextInput v-model="form.AllowedGrantTypes" placeholder="authorization_code, refresh_token, client_credentials" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.oauthClients.corsOrigins', {}, 'CORS Origins (eine pro Zeile)')">
          <textarea v-model="form.AllowedCorsOrigins" rows="3" class="textarea" />
        </CoarFormField>
      </div>

      <!-- Lifetimes -->
      <div v-show="!isCreate && activeTab === 'lifetimes'" class="tab-content">
        <p class="text-xs text-gray-500 mb-2">
          {{ t('admin.oauthClients.lifetimesHint', {}, 'Werte in Sekunden. Leer = Default des IdP.') }}
        </p>
        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.oauthClients.identityTokenLifetime', {}, 'Identity-Token')">
            <CoarTextInput v-model="form.IdentityTokenLifetime" type="number" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.accessTokenLifetime', {}, 'Access-Token')">
            <CoarTextInput v-model="form.AccessTokenLifetime" type="number" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.authCodeLifetime', {}, 'Authorization-Code')">
            <CoarTextInput v-model="form.AuthorizationCodeLifetime" type="number" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.absRefreshLifetime', {}, 'Absolute Refresh-Token')">
            <CoarTextInput v-model="form.AbsoluteRefreshTokenLifetime" type="number" clearable />
          </CoarFormField>
          <CoarFormField :label="t('admin.oauthClients.slidingRefreshLifetime', {}, 'Sliding Refresh-Token')">
            <CoarTextInput v-model="form.SlidingRefreshTokenLifetime" type="number" clearable />
          </CoarFormField>
        </div>
      </div>

      <p v-if="error" class="mt-3 text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.tab-bar {
  margin-bottom: 12px;
}
.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding-bottom: 16px;
  min-height: 0;
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
