<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useServiceAccountStore } from '@/stores/serviceAccount.store'
import {
  CoarNotice,
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarButton,
  CoarPopconfirm,
  CoarTag,
  CoarDivider,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import CredentialEditModal from './CredentialEditModal.vue'
import { MODAL_LIST_FORM } from '@/router/modal-sizes'
import { useModalOverlay } from '@/composables/useModalOverlay'
import { useHttpClient } from '@/composables/useHttpClient'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type { OAuthClientDto } from '@/models/oauth'
import type { ClientSecretDto } from '@/models/oauth'
import type { IssueServiceAccountCredentialDto, ServiceAccountCreateDto } from '@/models/serviceAccount'

const { t } = useI18n()
const toast = useToast()
const modalOverlay = useModalOverlay()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
  /**
   * Reuse the normal create dialog as an embedded draft editor. In this mode
   * Save returns the validated DTO to the parent without calling the API.
   */
  draftOnly?: boolean
  initial?: ServiceAccountCreateDto
}>()

const store = useServiceAccountStore()
const scopeStore = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

const form = ref({
  AccountName: props.initial?.AccountName ?? '',
  Purpose: props.initial?.Purpose ?? '',
  IsActive: props.initial?.IsActive ?? true,
})
const originalAccountName = ref('')
const originalIsActive = ref(true)
const accountNamePattern = /^[a-z0-9][a-z0-9._-]{1,63}$/

const accountNameError = computed(() => {
  const value = form.value.AccountName.trim()
  if (!value || !isCreate.value) return ''
  if (!accountNamePattern.test(value))
    return t(
      'admin.serviceAccounts.accountNameInvalid',
      {},
      '2–64 Zeichen; nur Kleinbuchstaben, Ziffern, Punkt, Bindestrich und Unterstrich.',
    )
  return ''
})

// Credentials section state. Credentials is the list of OAuth clients
// linked to this SA via LinkedServiceAccountId. Loaded on mount + after
// each mutation. Skipped on create-mode since no id exists yet.
const credentials = ref<OAuthClientDto[]>([])
const credentialsLoading = ref(false)
const credentialsHttp = computed(() => useHttpClient(`/api/service-account/${props.id}/credentials`))

const rotatedSecret = ref<string | null>(null)
const rotatedClientId = ref<string | null>(null)
const initialCredential = ref<IssueServiceAccountCredentialDto | null>(null)
const creationComplete = ref(false)

const modalTitle = computed(() => {
  return isCreate.value
    ? t('admin.serviceAccounts.createTitle', {}, 'Create service account')
    : (form.value.AccountName || t('admin.serviceAccounts.editTitle', {}, 'Service account'))
})

const footerButton = computed(() => ({
  visible: true,
  text: creationComplete.value
    ? t('common.done', {}, 'Fertig')
    : props.draftOnly
    ? t('admin.oauthClients.newServiceAccount.apply', {}, 'Übernehmen')
    : isCreate.value
      ? t('common.create', {}, 'Create')
      : t('common.save', {}, 'Save'),
  disabled: creationComplete.value
    ? false
    : !form.value.AccountName.trim() || !!accountNameError.value || loading.value,
  onClick: creationComplete.value ? () => props.close(true) : save,
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
  if (!form.value.AccountName.trim() || accountNameError.value) return
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      const createDto: ServiceAccountCreateDto = {
        AccountName: form.value.AccountName.trim(),
        Purpose: form.value.Purpose.trim() || undefined,
        IsActive: form.value.IsActive,
        InitialCredential: props.draftOnly ? undefined : initialCredential.value ?? undefined,
      }
      if (props.draftOnly) {
        props.close(createDto)
        return
      }
      const created = await store.createEntity(createDto)
      if (created.InitialCredential) {
        rotatedSecret.value = created.InitialCredential.ClientSecret
        rotatedClientId.value = created.InitialCredential.Credential.ClientId
        creationComplete.value = true
        return
      }
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

async function openInitialCredential() {
  const result = await modalOverlay.open<IssueServiceAccountCredentialDto>(
    CredentialEditModal,
    MODAL_LIST_FORM,
    {
      saId: 'create',
      id: 'create',
      draftOnly: true,
      initial: initialCredential.value ?? undefined,
    },
  )
  if (result) initialCredential.value = result
}

function removeInitialCredential() {
  initialCredential.value = null
}

// Opened from inside this modal, so there is no routed fragment for it — but
// it uses the same bare-overlay plumbing (see useModalOverlay: the CoarDialog
// shell would draw a second modal frame around the ModalLayout).
async function openCredentialModal(credentialId: string) {
  const result = await modalOverlay.open<boolean>(
    CredentialEditModal,
    MODAL_LIST_FORM,
    { saId: props.id, id: credentialId },
  )
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
    toast.success(t('admin.serviceAccountCredentials.deleted', {}, 'OAuth client deleted.'))
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
    <div v-if="creationComplete" class="flex flex-col gap-4 p-2">
      <CoarNotice variant="warning">
        <div class="flex flex-col gap-3">
          <div class="font-medium">
            {{ t('admin.serviceAccountCredentials.secretOnce', {}, 'Client Secret jetzt kopieren — es wird nicht erneut angezeigt.') }}
          </div>
          <div class="flex flex-col gap-1">
            <span class="text-xs uppercase tracking-wide text-surface-500">
              {{ t('admin.serviceAccountCredentials.clientId', {}, 'Client ID') }}
            </span>
            <code class="text-sm">{{ rotatedClientId }}</code>
          </div>
          <div class="flex items-center gap-2">
            <code class="flex-1 break-all text-sm">{{ rotatedSecret }}</code>
            <CoarButton size="s" icon-start="copy" @click="copyRotatedSecret">
              {{ t('common.copy', {}, 'Kopieren') }}
            </CoarButton>
          </div>
        </div>
      </CoarNotice>
    </div>
    <div v-else-if="!loading || isCreate" class="service-account-editor">
      <div class="modal-form">
        <!-- Section: Basis -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.serviceAccounts.section.basics', {}, 'Basis') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full" :label="t('admin.serviceAccounts.accountName', {}, 'Account name')" required
              :error="accountNameError"
              :hint="t('admin.serviceAccounts.accountNameHint', {}, 'Lowercase letters, digits, dots, hyphens or underscores. Becomes the audit-log handle for this account.')">
              <CoarTextInput v-model="form.AccountName" clearable :disabled="!isCreate"
                :placeholder="t('admin.serviceAccounts.accountNamePlaceholder', {}, 'ci.build-agent, integrations.acme, …')" />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.serviceAccounts.purpose', {}, 'Purpose')"
              :hint="t('admin.serviceAccounts.purposeHint', {}, 'Free text describing what this service account is used for. Optional.')">
              <CoarTextInput v-model="form.Purpose" clearable
                :placeholder="t('admin.serviceAccounts.purposePlaceholder', {}, 'CI deployment, nightly sync, …')" />
            </CoarFormField>
          </div>
        </section>

        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.serviceAccounts.section.status', {}, 'Status') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full"
              :label="t('admin.serviceAccounts.active', {}, 'Aktiv')"
              :hint="t('admin.serviceAccounts.activeHint', {}, 'Inactive accounts can no longer authenticate — existing tokens stay valid until expiry, but no new ones are issued.')"
              layout="inline"
              label-position="after">
              <CoarCheckbox v-model="form.IsActive" />
            </CoarFormField>
          </div>
        </section>
      </div>

      <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

      <section class="form-section">
        <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
          <h3 class="section-divider__title">
            {{ t('admin.serviceAccountCredentials.sectionTitle', {}, 'OAuth clients') }}
          </h3>
        </CoarDivider>

        <div class="mb-3 flex items-center gap-3">
          <CoarNotice truncate variant="info" class="min-w-0 flex-1">
            {{ props.draftOnly
              ? t('admin.serviceAccountCredentials.outerClientHint', {}, 'The OAuth client being configured will be linked to this service account.')
              : t('admin.serviceAccountCredentials.sectionHintShort', {}, 'OAuth clients of this Service Account.') }}
            <template #details>
              {{ t('admin.serviceAccountCredentials.sectionHint', {}, 'Each OAuth client authenticates with its own client ID and secret at /connect/token, but shares this service account\'s permissions and group memberships.') }}
            </template>
          </CoarNotice>
          <!-- shrink-0: the label is `white-space:nowrap; overflow:hidden`, so a
               shrinking button silently cuts its own text off. -->
          <CoarButton v-if="!props.draftOnly" size="s" :icon-start="initialCredential && isCreate ? 'pencil' : 'plus'"
            class="shrink-0" @click="isCreate ? openInitialCredential() : openCredentialModal('create')">
            {{ initialCredential && isCreate
              ? t('admin.serviceAccountCredentials.editInitialButton', {}, 'Edit OAuth client')
              : t('admin.serviceAccountCredentials.issueButton', {}, 'Add OAuth client') }}
          </CoarButton>
        </div>

        <div v-if="isCreate && initialCredential" class="initial-credential">
          <div class="initial-credential__body">
            <strong>{{ initialCredential.DisplayName || t('admin.serviceAccountCredentials.initialDefaultName', {}, 'Initial OAuth client') }}</strong>
            <span>
              {{ initialCredential.Scopes.length }}
              {{ t('admin.serviceAccountCredentials.scopes', {}, 'Scopes') }}
              ·
              {{ initialCredential.AppIds.length }}
              {{ t('admin.serviceAccountCredentials.apps', {}, 'Apps') }}
            </span>
          </div>
          <CoarTag :variant="initialCredential.Enabled === false ? 'warning' : 'success'">
            {{ initialCredential.Enabled === false
              ? t('admin.serviceAccountCredentials.disabled', {}, 'Deaktiviert')
              : t('admin.serviceAccountCredentials.enabled', {}, 'Aktiv') }}
          </CoarTag>
          <CoarButton size="s" variant="ghost" icon-start="trash-2" @click="removeInitialCredential">
            {{ t('common.remove', {}, 'Entfernen') }}
          </CoarButton>
        </div>

        <!-- Rotated-secret panel (shown after rotate; dismissable). -->
        <CoarNotice v-if="!isCreate && rotatedSecret" variant="warning" class="mb-3">
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
        </CoarNotice>

        <div v-if="isCreate && !initialCredential && !props.draftOnly" class="credential-empty">
          {{ t('admin.serviceAccountCredentials.initialEmpty', {}, 'No initial OAuth client configured yet.') }}
        </div>
        <div v-else-if="isCreate && props.draftOnly" class="credential-empty">
          {{ t('admin.serviceAccountCredentials.outerClientConfigured', {}, 'Scopes, apps, token settings, and the secret are configured in the parent OAuth client.') }}
        </div>
        <template v-if="!isCreate">
          <div v-if="credentialsLoading" class="text-xs text-surface-500">
            {{ t('common.loading', {}, 'Loading...') }}
          </div>
          <div v-else-if="credentials.length === 0" class="credential-empty">
            {{ t('admin.serviceAccountCredentials.empty', {}, 'No OAuth clients yet.') }}
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
                :title="t('admin.serviceAccountCredentials.deleteTitle', {}, 'Delete OAuth client?')"
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
        </template>
      </section>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
.service-account-editor {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
  min-width: 0;
  padding: 0.25rem;
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

.credential-empty {
  padding: 1rem;
  border: 1px dashed var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: 0.25rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.875rem;
  text-align: center;
}

.initial-credential {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.75rem;
  border: 1px solid var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: 0.25rem;
}

.initial-credential__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 0.2rem;
  min-width: 0;
}

.initial-credential__body span {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.75rem;
}
</style>
