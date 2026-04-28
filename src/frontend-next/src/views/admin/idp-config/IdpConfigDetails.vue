<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import {
  CoarTextInput, CoarFormField, CoarCheckbox, CoarTabGroup, CoarTab,
  CoarButton, CoarIcon,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useIdpConfigStore } from '@/stores/idpConfig.store'
import type { FlavorDto, IdpConfigDto } from '@/models/idpConfig'
import UserUpdateScriptEditor from './UserUpdateScriptEditor.vue'
import FlavorConnectionPanel from './panels/FlavorConnectionPanel.vue'

const { t } = useI18n()
const store = useIdpConfigStore()

const props = defineProps<{ id: string; close: (result?: unknown) => void }>()
const isCreate = computed(() => props.id === 'create')

const activeTab = ref<'general' | 'connection' | 'claims' | 'linking'>('general')
const loading = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)

const config = ref<IdpConfigDto | null>(null)
const flavor = computed<FlavorDto | null>(() =>
  store.flavors.find((f) => f.Key === config.value?.Flavor) ?? null
)

const form = ref({
  DisplayName: '',
  ClientId: '',
  Scopes: [] as string[],
  UserUpdateScript: '',
  StoreRawClaims: false,
  RawClaimsRetentionDays: null as number | null,
  AutoCreateUsers: false,
  AllowLinking: true,
  TrustForEmailLink: false,
  AllowedEmailDomains: [] as string[],
  IconName: '',
  ButtonColorHex: '',
  FlavorData: {} as Record<string, unknown>,
})

const newSecret = ref<string>('')

const modalTitle = computed(() => {
  if (isCreate.value) return t('admin.idpConfig.createTitle', {}, 'New identity provider')
  return config.value?.DisplayName || ''
})

const redirectUri = computed(() => config.value?.RedirectUri ?? '')

async function load() {
  loading.value = true
  try {
    await store.initialize()
    const existing = store.configs.find((c) => c.Id === props.id)
      ?? await store.loadOne(props.id)
    if (!existing) {
      error.value = t('admin.idpConfig.notFound', {}, 'Not found')
      return
    }
    config.value = existing
    form.value = {
      DisplayName: existing.DisplayName,
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
      FlavorData: existing.FlavorData ? { ...existing.FlavorData } : {},
    }
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!config.value) return
  if (!form.value.DisplayName.trim()) {
    error.value = t('admin.idpConfig.nameRequired', {}, 'Name is required')
    return
  }
  error.value = null
  saving.value = true
  try {
    await store.update(config.value.Id, {
      DisplayName: form.value.DisplayName.trim(),
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
  } catch (e: any) {
    error.value = e?.response?.data?.Message ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

async function rotateSecret() {
  if (!config.value || !newSecret.value) return
  saving.value = true
  error.value = null
  try {
    await store.rotateSecret(config.value.Id, newSecret.value)
    newSecret.value = ''
    const reloaded = await store.loadOne(config.value.Id)
    if (reloaded) config.value = reloaded
  } catch (e: any) {
    error.value = e?.response?.data?.Message ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

async function toggleEnabled() {
  if (!config.value) return
  saving.value = true
  error.value = null
  try {
    const updated = config.value.Enabled
      ? await store.disable(config.value.Id)
      : await store.enable(config.value.Id)
    config.value = updated
  } catch (e: any) {
    error.value = e?.response?.data?.Message ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

function copyRedirect() {
  if (redirectUri.value) navigator.clipboard?.writeText(redirectUri.value)
}

const footerButton = computed(() => ({
  visible: !isCreate.value,
  text: t('common.save', {}, 'Save'),
  disabled: saving.value || loading.value,
  onClick: save,
}))

onMounted(load)

// When flavor changes (it shouldn't after creation, but guard anyway),
// reset any stale flavor-data.
watch(() => flavor.value?.Key, () => { /* no-op for now */ })

function parseList(s: string): string[] {
  return s.split(/[\s,]+/).map((p) => p.trim()).filter(Boolean)
}
</script>

<template>
  <ModalLayout
    :close="close"
    :title="modalTitle"
    icon="key-round"
    :footer-button="footerButton"
    width="min(1100px, 95vw)"
  >
    <div v-if="loading" class="flex items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>

    <div v-else-if="config" class="flex flex-col flex-1 min-h-0 gap-3">
      <!-- Header row: flavor, enabled-toggle, error -->
      <div class="flex items-center gap-3 flex-wrap">
        <div class="badge">
          <CoarIcon :name="flavor?.DefaultIconName ?? 'key-round'" size="s" />
          <span>{{ flavor?.DisplayName ?? config.Flavor }}</span>
        </div>
        <CoarButton
          size="s"
          :variant="config.Enabled ? 'solid' : 'subtle'"
          :icon-start="config.Enabled ? 'circle-check' : 'circle-off'"
          :disabled="saving"
          @click="toggleEnabled"
        >
          {{ config.Enabled ? t('admin.idpConfig.enabled', {}, 'Enabled') : t('admin.idpConfig.disabled', {}, 'Disabled') }}
        </CoarButton>
        <div v-if="error" class="error-banner flex-1">{{ error }}</div>
      </div>

      <CoarTabGroup v-model="activeTab">
        <CoarTab id="general">{{ t('admin.idpConfig.tabGeneral', {}, 'General') }}</CoarTab>
        <CoarTab id="connection">{{ t('admin.idpConfig.tabConnection', {}, 'Connection') }}</CoarTab>
        <CoarTab id="claims">{{ t('admin.idpConfig.tabUserUpdate', {}, 'User update') }}</CoarTab>
        <CoarTab id="linking">{{ t('admin.idpConfig.tabLinking', {}, 'Linking & policy') }}</CoarTab>
      </CoarTabGroup>

      <!-- General tab -->
      <div v-show="activeTab === 'general'" class="tab-content">
        <CoarFormField :label="t('admin.idpConfig.name', {}, 'Display name')">
          <CoarTextInput v-model="form.DisplayName" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.idpConfig.redirectUri', {}, 'Redirect URI (copy into IdP app registration)')">
          <div class="flex gap-2 items-center">
            <CoarTextInput :model-value="redirectUri" readonly class="flex-1" />
            <CoarButton size="s" variant="subtle" icon-start="copy" @click="copyRedirect">
              {{ t('common.copy', {}, 'Copy') }}
            </CoarButton>
          </div>
        </CoarFormField>
        <CoarFormField :label="t('admin.idpConfig.iconName', {}, 'Button icon name (Lucide)')">
          <CoarTextInput v-model="form.IconName" placeholder="microsoft" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.idpConfig.buttonColorHex', {}, 'Button color (hex, optional)')">
          <CoarTextInput v-model="form.ButtonColorHex" placeholder="#0078D4" clearable />
        </CoarFormField>
      </div>

      <!-- Connection tab -->
      <div v-show="activeTab === 'connection'" class="tab-content">
        <FlavorConnectionPanel
          v-if="flavor"
          :schema="flavor.ConfigSchema"
          v-model="form.FlavorData"
        />
        <CoarFormField :label="t('admin.idpConfig.clientId', {}, 'Client ID')">
          <CoarTextInput v-model="form.ClientId" placeholder="application-client-id" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.idpConfig.scopes', {}, 'Scopes (space- or comma-separated)')">
          <CoarTextInput
            :model-value="form.Scopes.join(' ')"
            placeholder="openid profile email"
            clearable
            @update:model-value="(v: string) => form.Scopes = parseList(v)"
          />
        </CoarFormField>

        <div class="secret-section">
          <div class="section-heading">{{ t('admin.idpConfig.clientSecret', {}, 'Client secret') }}</div>
          <div class="text-sm secret-status mb-2">
            <CoarIcon :name="config.HasClientSecret ? 'shield-check' : 'shield-alert'" size="s" />
            <span v-if="config.HasClientSecret">{{ t('admin.idpConfig.secretSet', {}, 'Secret is configured. Enter a new value below to rotate.') }}</span>
            <span v-else>{{ t('admin.idpConfig.secretMissing', {}, 'No secret configured — set one before enabling the provider.') }}</span>
          </div>
          <div class="flex gap-2">
            <CoarTextInput v-model="newSecret" type="password" class="flex-1" clearable placeholder="••••••••" />
            <CoarButton :disabled="!newSecret || saving" @click="rotateSecret">
              {{ config.HasClientSecret ? t('admin.idpConfig.rotateSecret', {}, 'Rotate') : t('admin.idpConfig.setSecret', {}, 'Set') }}
            </CoarButton>
          </div>
        </div>
      </div>

      <!-- User-update script tab -->
      <div v-show="activeTab === 'claims'" class="tab-content tab-claims">
        <UserUpdateScriptEditor
          v-model="form.UserUpdateScript"
          :idp-config-id="config.Id"
          :is-new="false"
        />
      </div>

      <!-- Linking + policy tab -->
      <div v-show="activeTab === 'linking'" class="tab-content">
        <CoarCheckbox v-model="form.AutoCreateUsers" :label="t('admin.idpConfig.autoCreate', {}, 'Auto-create users on first login (JIT)')" />
        <CoarCheckbox v-model="form.AllowLinking" :label="t('admin.idpConfig.allowLinking', {}, 'Allow users to link this IdP from profile')" />
        <CoarCheckbox v-model="form.TrustForEmailLink" :label="t('admin.idpConfig.trustForEmailLink', {}, 'Trust-for-email-link — auto-bind to existing local user with same email (DANGEROUS: only for tenant-owned IdPs)')" />

        <CoarFormField :label="t('admin.idpConfig.allowedEmailDomains', {}, 'Allowed email domains (comma-separated, empty = no filter)')">
          <CoarTextInput
            :model-value="form.AllowedEmailDomains.join(', ')"
            placeholder="acme.com, contoso.com"
            clearable
            @update:model-value="(v: string) => form.AllowedEmailDomains = parseList(v)"
          />
        </CoarFormField>

        <div class="divider"></div>

        <CoarCheckbox v-model="form.StoreRawClaims" :label="t('admin.idpConfig.storeRawClaims', {}, 'Store raw claim snapshots per login (for debugging)')" />
        <CoarFormField v-if="form.StoreRawClaims" :label="t('admin.idpConfig.rawRetentionDays', {}, 'Raw claims retention (days, empty = forever)')">
          <CoarTextInput
            :model-value="form.RawClaimsRetentionDays?.toString() ?? ''"
            type="number"
            clearable
            @update:model-value="(v: string) => form.RawClaimsRetentionDays = v ? Number(v) : null"
          />
        </CoarFormField>
      </div>
    </div>

    <div v-else class="error-banner m-4">{{ error ?? t('admin.idpConfig.notFound', {}, 'Not found') }}</div>
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
