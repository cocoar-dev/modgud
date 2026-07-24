<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useServiceAccountStore } from '@/stores/serviceAccount.store'
import {
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarButton,
  CoarPopconfirm,
  CoarTag,
  useDialog,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import AppNote from '@/components/AppNote.vue'
import CredentialEditModal from './CredentialEditModal.vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type { OAuthClientDto } from '@/models/oauth'
import type { ClientSecretDto } from '@/models/oauth'

const { t } = useI18n()
const toast = useToast()
const dialog = useDialog()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useServiceAccountStore()
const scopeStore = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

const form = ref({
  AccountName: '',
  Purpose: '',
  IsActive: true,
})
const originalAccountName = ref('')
const originalIsActive = ref(true)

// Credentials section state. Credentials is the list of OAuth clients
// linked to this SA via LinkedServiceAccountId. Loaded on mount + after
// each mutation. Skipped on create-mode since no id exists yet.
const credentials = ref<OAuthClientDto[]>([])
const credentialsLoading = ref(false)
const credentialsHttp = computed(() => useHttpClient(`/api/service-account/${props.id}/credentials`))

const rotatedSecret = ref<string | null>(null)
const rotatedClientId = ref<string | null>(null)

const modalTitle = computed(() => {
  return isCreate.value
    ? t('admin.serviceAccounts.createTitle', {}, 'Create service account')
    : (form.value.AccountName || t('admin.serviceAccounts.editTitle', {}, 'Service account'))
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.AccountName.trim() || loading.value,
  onClick: save,
}))

onMounted(async () => {
  // Pre-load reference data the credentials sub-modal needs — doing it from
  // here means the sub-modal opens with picker data already in memory instead
  // of showing empty lists on first paint.
  if (!isCreate.value) {
    void Promise.all([
      scopeStore.scopes.length === 0 ? scopeStore.loadAll() : Promise.resolve(),
      applicationsStore.apps.length === 0 ? applicationsStore.loadAll() : Promise.resolve(),
    ])
  }

  if (!isCreate.value) {
    loading.value = true
    try {
      const sa = await store.getById(props.id)
      form.value = {
        AccountName: sa.AccountName,
        Purpose: sa.Purpose ?? '',
        IsActive: sa.IsActive,
      }
      originalAccountName.value = sa.AccountName
      originalIsActive.value = sa.IsActive
      await loadCredentials()
    } catch (e: unknown) {
      const err = e as { data?: { Message?: string }; message?: string }
      error.value = err?.data?.Message ?? err?.message ?? String(e)
    } finally {
      loading.value = false
    }
  }
})

async function loadCredentials() {
  if (isCreate.value) return
  credentialsLoading.value = true
  try {
    credentials.value = await credentialsHttp.value.get<OAuthClientDto[]>()
  } finally {
    credentialsLoading.value = false
  }
}

async function save() {
  if (!form.value.AccountName.trim()) return
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      await store.createEntity({
        AccountName: form.value.AccountName.trim(),
        Purpose: form.value.Purpose.trim() || undefined,
      })
    } else {
      // Send only fields that actually changed. Treat empty string in Purpose
      // as explicit clear (server normalises blank to null).
      const body: Record<string, unknown> = {
        Purpose: form.value.Purpose.trim() === '' ? null : form.value.Purpose.trim(),
      }
      if (form.value.AccountName.trim() !== originalAccountName.value) {
        body.AccountName = form.value.AccountName.trim()
      }
      if (form.value.IsActive !== originalIsActive.value) {
        body.IsActive = form.value.IsActive
      }
      await store.httpClient.addPath(props.id).put(body)
    }
    props.close()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    error.value = err?.data?.Message ?? err?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

async function openCredentialModal(credentialId: string) {
  const ref$ = dialog.open<boolean>(CredentialEditModal, {
    title: credentialId === 'create'
      ? t('admin.serviceAccountCredentials.issueTitle', {}, 'Issue credential')
      : t('admin.serviceAccountCredentials.editTitle', {}, 'Edit credential'),
    size: 'l',
  }, { saId: props.id, id: credentialId })
  const result = await ref$.result
  if (result) {
    await loadCredentials()
  }
}

async function rotateCredential(cred: OAuthClientDto) {
  try {
    const res = await credentialsHttp.value.addPath(cred.Id, 'rotate').post<ClientSecretDto>()
    rotatedSecret.value = res.ClientSecret
    rotatedClientId.value = cred.ClientId
    toast.success(t('admin.serviceAccountCredentials.rotated', {}, 'Secret rotated. Copy the new value now — it will not be shown again.'))
    await loadCredentials()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function deleteCredential(cred: OAuthClientDto) {
  try {
    await credentialsHttp.value.addPath(cred.Id).delete()
    toast.success(t('admin.serviceAccountCredentials.deleted', {}, 'Credential deleted.'))
    await loadCredentials()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function copyRotatedSecret() {
  if (!rotatedSecret.value) return
  await navigator.clipboard.writeText(rotatedSecret.value)
}

function dismissRotatedSecret() {
  rotatedSecret.value = null
  rotatedClientId.value = null
}

function extractScopes(cred: OAuthClientDto): string[] {
  return cred.Permissions
    .filter((p) => p.startsWith('scp:'))
    .map((p) => p.slice('scp:'.length))
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="cpu" :footer-button="footerButton">
    <div v-if="!loading || isCreate" class="flex flex-col gap-4 p-1">
      <div class="modal-form">
        <!-- Section: Basis -->
        <section class="form-section">
          <h3 class="form-section-heading">{{ t('admin.serviceAccounts.section.basics', {}, 'Basics') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-half" :label="t('admin.serviceAccounts.accountName', {}, 'Account name')" required
              :hint="t('admin.serviceAccounts.accountNameHint', {}, 'Lowercase letters, digits, dots, hyphens or underscores. Becomes the audit-log handle for this account.')">
              <CoarTextInput v-model="form.AccountName" clearable :disabled="!isCreate"
                :placeholder="t('admin.serviceAccounts.accountNamePlaceholder', {}, 'ci.build-agent, integrations.acme, …')" />
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.serviceAccounts.purpose', {}, 'Purpose')"
              :hint="t('admin.serviceAccounts.purposeHint', {}, 'Free text describing what this service account is used for. Optional.')">
              <CoarTextInput v-model="form.Purpose" clearable
                :placeholder="t('admin.serviceAccounts.purposePlaceholder', {}, 'CI deployment, nightly sync, …')" />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Status — edit-only; an existing SA can be deactivated. -->
        <section v-if="!isCreate" class="form-section">
          <h3 class="form-section-heading">{{ t('admin.serviceAccounts.section.status', {}, 'Status') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-full" :label="t('admin.serviceAccounts.activeLabel', {}, 'Status')"
              :hint="t('admin.serviceAccounts.activeHint', {}, 'Inactive accounts can no longer authenticate — existing tokens stay valid until expiry, but no new ones are issued.')">
              <CoarCheckbox v-model="form.IsActive" :label="t('admin.serviceAccounts.active', {}, 'Active')" />
            </CoarFormField>
          </div>
        </section>
      </div>

      <AppNote v-if="error" variant="error" :truncate="false">{{ error }}</AppNote>

      <!-- Credentials section — only visible when editing an existing SA.
           On create, the SA has to be saved first before credentials can be
           issued (the API needs a persisted SA.Id to link against). -->
      <section v-if="!isCreate" class="mt-2 border-t border-surface-200 pt-4">
        <div class="mb-3 flex items-center justify-between gap-3">
          <div>
            <h3 class="text-base font-medium">
              {{ t('admin.serviceAccountCredentials.sectionTitle', {}, 'Credentials') }}
            </h3>
            <p class="text-xs text-surface-500">
              {{ t('admin.serviceAccountCredentials.sectionHint', {}, 'OAuth clients owned by this Service Account. Each credential authenticates separately at /connect/token but shares this SA\'s permissions and group memberships.') }}
            </p>
          </div>
          <CoarButton size="s" icon-start="plus" @click="openCredentialModal('create')">
            {{ t('admin.serviceAccountCredentials.issueButton', {}, 'Issue credential') }}
          </CoarButton>
        </div>

        <!-- Rotated-secret panel (shown after rotate; dismissable). -->
        <AppNote v-if="rotatedSecret" variant="warning" :truncate="false" class="mb-3">
          <div class="flex flex-col gap-2">
            <div class="font-medium">
              {{ t('admin.serviceAccountCredentials.rotatedTitle', {}, 'New secret for') }}
              <code class="text-sm">{{ rotatedClientId }}</code>
            </div>
            <div class="flex items-center gap-2">
              <code class="flex-1 break-all text-sm">{{ rotatedSecret }}</code>
              <CoarButton size="s" icon-start="copy" @click="copyRotatedSecret">
                {{ t('common.copy', {}, 'Copy') }}
              </CoarButton>
              <CoarButton size="s" variant="ghost" @click="dismissRotatedSecret">
                {{ t('common.dismiss', {}, 'Dismiss') }}
              </CoarButton>
            </div>
          </div>
        </AppNote>

        <div v-if="credentialsLoading" class="text-xs text-surface-500">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>
        <div v-else-if="credentials.length === 0" class="rounded border border-dashed border-surface-300 p-4 text-center text-sm text-surface-500">
          {{ t('admin.serviceAccountCredentials.empty', {}, 'No credentials yet. Issue one to let services authenticate as this account.') }}
        </div>
        <ul v-else class="flex flex-col gap-2">
          <li v-for="cred in credentials" :key="cred.Id"
              class="flex flex-col gap-2 rounded border border-surface-200 p-3">
            <div class="flex flex-wrap items-baseline gap-2">
              <code class="text-sm font-medium">{{ cred.ClientId }}</code>
              <span v-if="cred.DisplayName" class="text-xs text-surface-500">— {{ cred.DisplayName }}</span>
              <CoarTag v-if="!cred.Enabled" variant="warning">
                {{ t('admin.serviceAccountCredentials.disabled', {}, 'Disabled') }}
              </CoarTag>
            </div>
            <div class="flex flex-wrap items-center gap-1.5 text-xs text-surface-500">
              <span v-if="extractScopes(cred).length > 0" class="flex flex-wrap gap-1">
                <CoarTag v-for="s in extractScopes(cred)" :key="s">{{ s }}</CoarTag>
              </span>
              <span v-else>{{ t('admin.serviceAccountCredentials.noScopes', {}, 'No scopes set') }}</span>
              <span class="mx-1">·</span>
              <span>{{ cred.AppIds.length }} {{ t('admin.serviceAccountCredentials.appsLinked', {}, 'app(s)') }}</span>
              <span v-if="cred.AccessTokenLifetime != null" class="mx-1">·</span>
              <span v-if="cred.AccessTokenLifetime != null">{{ cred.AccessTokenLifetime }}s</span>
            </div>
            <div class="flex items-center justify-end gap-1">
              <CoarButton size="s" variant="ghost" icon-start="pencil" @click="openCredentialModal(cred.Id)">
                {{ t('common.edit', {}, 'Edit') }}
              </CoarButton>
              <CoarPopconfirm
                :title="t('admin.serviceAccountCredentials.rotateTitle', {}, 'Rotate secret?')"
                :message="t('admin.serviceAccountCredentials.rotateConfirm', {}, 'The old secret stops working immediately and the new one is shown only once.')"
                @confirmed="rotateCredential(cred)">
                <CoarButton size="s" variant="ghost" icon-start="rotate-ccw">
                  {{ t('admin.serviceAccountCredentials.rotateButton', {}, 'Rotate') }}
                </CoarButton>
              </CoarPopconfirm>
              <CoarPopconfirm
                :title="t('admin.serviceAccountCredentials.deleteTitle', {}, 'Delete credential?')"
                :message="t('admin.serviceAccountCredentials.deleteConfirm', {}, 'Existing tokens stay valid until expiry but no new tokens can be minted.')"
                confirm-variant="danger"
                @confirmed="deleteCredential(cred)">
                <CoarButton size="s" variant="ghost" icon-start="trash-2">
                  {{ t('common.delete', {}, 'Delete') }}
                </CoarButton>
              </CoarPopconfirm>
            </div>
          </li>
        </ul>
      </section>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>
