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
import { useDraftStaging } from '@/composables/useDraftStaging'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type { ManifestEntity } from '@/stores/realmDraft.store'
import type { OAuthClientDto, AccessTokenType } from '@/models/oauth'
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

// ── ADR-0017 staging: the account and its credentials' CONFIGURATION — display
// name, scopes, apps, enabled, token settings — commit onto the active draft as
// one ServiceAccounts[] entity with its Credentials list. The plan shows every
// change (a removed credential in red), the apply writes them through the
// SA-scoped ops, and a credential staged here is minted its secret at apply and
// handed back once in the apply result. What stays live is credential MATERIAL:
// rotating a secret. The embedded draftOnly flow belongs to the parent client
// create and stays untouched.
const staging = useDraftStaging('serviceAccounts')
const isDraftRow = computed(() => staging.isDraftId(props.id))
const stagedSave = computed(() => staging.stagingActive.value && !props.draftOnly)
/** The section's natural key: the lowercased account name. */
const stagingKey = computed(() => form.value.AccountName.trim().toLowerCase())
/** The staged entity's credentials in MANIFEST shape (ClientId, Id, DisplayName,
 * Scopes, Apps as slugs, Enabled, AccessTokenType, AccessTokenLifetime). */
const stagedCredentials = ref<ManifestEntity[]>([])
/** Credentials is a desired set — only ever written once the list was really loaded. */
const stagedCredentialsLoaded = ref(false)

function appSlugsOf(appIds: string[]): string[] {
  return appIds
    .map((id) => applicationsStore.apps.find((a) => a.Id === id)?.Slug)
    .filter((s): s is string => !!s)
}
function appIdsOf(slugs: unknown): string[] {
  return (Array.isArray(slugs) ? (slugs as string[]) : [])
    .map((slug) => applicationsStore.apps.find((a) => a.Slug === slug)?.Id)
    .filter((id): id is string => !!id)
}
function credentialToManifest(cred: OAuthClientDto): ManifestEntity {
  return {
    Id: cred.Id,
    ClientId: cred.ClientId,
    DisplayName: cred.DisplayName ?? null,
    Scopes: extractScopes(cred),
    Apps: appSlugsOf(cred.AppIds),
    Enabled: cred.Enabled,
    AccessTokenType: cred.AccessTokenType ?? 'Reference',
    AccessTokenLifetime: cred.AccessTokenLifetime ?? null,
  }
}
function issueDtoToManifest(dto: IssueServiceAccountCredentialDto, clientId: string): ManifestEntity {
  return {
    ClientId: clientId,
    DisplayName: dto.DisplayName?.trim() || null,
    Scopes: [...dto.Scopes],
    Apps: appSlugsOf(dto.AppIds),
    Enabled: dto.Enabled ?? true,
    AccessTokenType: dto.AccessTokenType ?? 'Reference',
    AccessTokenLifetime: dto.AccessTokenLifetime ?? null,
  }
}
/** Same convention the server uses when it mints one: `{accountName}.{8 chars}`. */
function newClientId(): string {
  const bytes = new Uint8Array(8)
  crypto.getRandomValues(bytes)
  const suffix = Array.from(bytes, (b) => (b % 36).toString(36)).join('')
  return `${stagingKey.value}.${suffix}`
}

/** One row per credential for the list — from the staged list with a draft open,
 * from the live list otherwise. `live` is the OAuth client behind it, if any. */
interface CredentialRow {
  key: string
  Id: string | null
  ClientId: string
  DisplayName: string | null
  Enabled: boolean
  Scopes: string[]
  AppCount: number
  AccessTokenLifetime: number | null
  live: OAuthClientDto | null
}
const credentialRows = computed<CredentialRow[]>(() => stagedSave.value
  ? stagedCredentials.value.map((c, i) => ({
      key: typeof c.ClientId === 'string' ? c.ClientId : String(i),
      Id: typeof c.Id === 'string' ? c.Id : null,
      ClientId: typeof c.ClientId === 'string' ? c.ClientId : '',
      DisplayName: typeof c.DisplayName === 'string' ? c.DisplayName : null,
      Enabled: c.Enabled !== false,
      Scopes: Array.isArray(c.Scopes) ? (c.Scopes as string[]) : [],
      AppCount: Array.isArray(c.Apps) ? c.Apps.length : 0,
      AccessTokenLifetime: typeof c.AccessTokenLifetime === 'number' ? c.AccessTokenLifetime : null,
      live: credentials.value.find((l) => l.Id === c.Id) ?? null,
    }))
  : credentials.value.map((c) => ({
      key: c.Id,
      Id: c.Id,
      ClientId: c.ClientId,
      DisplayName: c.DisplayName ?? null,
      Enabled: c.Enabled,
      Scopes: extractScopes(c),
      AppCount: c.AppIds.length,
      AccessTokenLifetime: c.AccessTokenLifetime ?? null,
      live: c,
    })))

function toStaged(): ManifestEntity {
  const entity: ManifestEntity = { ...(staging.findStaged(stagingKey.value) ?? {}) }
  entity.AccountName = stagingKey.value
  // v2 merge-patch: explicit null stages the clear (absent would keep live).
  entity.Purpose = form.value.Purpose.trim() || null
  entity.IsActive = form.value.IsActive
  // The desired set of credentials: the apply upserts what is listed and removes
  // what the account has but the list does not — the plan shows each removal.
  if (stagedCredentialsLoaded.value) entity.Credentials = stagedCredentials.value
  // Stage the LIVE entity's id: the apply matches by identity (ADR 0024).
  if (!isCreate.value && !isDraftRow.value) entity.Id = props.id
  return entity
}

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
    : stagedSave.value
      ? t('admin.realmConfig.entry.save', {}, 'Stage into draft')
    : isCreate.value
      ? t('common.create', {}, 'Create')
      : t('common.save', {}, 'Save'),
  disabled: creationComplete.value
    ? false
    : !form.value.AccountName.trim() || !!accountNameError.value || loading.value,
  onClick: creationComplete.value ? () => props.close(true) : save,
}))

onMounted(async () => {
  // Reference data the credentials sub-modal needs — and, with a draft open, the
  // app-id ↔ slug mapping the manifest shape is written in. Awaited in staging
  // mode so the mapping is complete before the first credential is staged.
  const referenceData = Promise.all([
    scopeStore.scopes.length === 0 ? scopeStore.loadAll() : Promise.resolve(),
    applicationsStore.apps.length === 0 ? applicationsStore.loadAll() : Promise.resolve(),
  ])
  if (stagedSave.value) await referenceData
  else if (!isCreate.value) void referenceData

  if (isDraftRow.value) {
    // Draft-created account: the staged manifest entity IS the state.
    const entity = staging.findStaged(staging.draftKeyOf(props.id))
    if (entity) {
      const str = (v: unknown) => (typeof v === 'string' ? v : '')
      form.value = {
        AccountName: str(entity.AccountName),
        Purpose: str(entity.Purpose),
        IsActive: entity.IsActive !== false,
      }
      stagedCredentials.value = Array.isArray(entity.Credentials)
        ? [...(entity.Credentials as ManifestEntity[])]
        : []
    }
    stagedCredentialsLoaded.value = true
    return
  }
  if (isCreate.value) {
    stagedCredentialsLoaded.value = true
    return
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
      // Staging overlay: the draft's entity is the working state — for the hull
      // and for the credential list, which starts from the live clients when the
      // draft does not carry the account yet.
      if (stagedSave.value) {
        const entity = staging.findStaged(sa.AccountName.trim().toLowerCase())
        if (entity) {
          if (typeof entity.Purpose === 'string') form.value.Purpose = entity.Purpose
          else if (entity.Purpose === null) form.value.Purpose = ''
          if (typeof entity.IsActive === 'boolean') form.value.IsActive = entity.IsActive
        }
        stagedCredentials.value = entity && Array.isArray(entity.Credentials)
          ? [...(entity.Credentials as ManifestEntity[])]
          : credentials.value.map(credentialToManifest)
        stagedCredentialsLoaded.value = true
      }
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
    // ADR-0017: commit onto the active draft instead of writing live.
    if (stagedSave.value) {
      await staging.stage(stagingKey.value, toStaged())
      props.close()
      return
    }
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

/** Staged add / edit of a credential: the sub-modal hands the DTO back, the
 * parent keeps it in manifest shape on the staged entity. */
async function openStagedCredential(row?: CredentialRow) {
  const staged = row ? stagedCredentials.value.find((c) => c.ClientId === row.ClientId) : undefined
  const initial: IssueServiceAccountCredentialDto | undefined = row
    ? {
        ClientId: row.ClientId,
        DisplayName: row.DisplayName ?? undefined,
        Scopes: [...row.Scopes],
        AppIds: appIdsOf(staged?.Apps),
        AccessTokenLifetime: row.AccessTokenLifetime ?? undefined,
        AccessTokenType: (typeof staged?.AccessTokenType === 'string'
          ? staged.AccessTokenType as AccessTokenType
          : 'Reference'),
        Enabled: row.Enabled,
      }
    : undefined
  const result = await modalOverlay.open<IssueServiceAccountCredentialDto>(
    CredentialEditModal,
    MODAL_LIST_FORM,
    { saId: props.id, id: row ? row.ClientId : 'create', draftOnly: true, stagedEdit: !!row, initial },
  )
  if (!result) return
  if (row) {
    stagedCredentials.value = stagedCredentials.value.map((c) => c.ClientId === row.ClientId
      ? { ...c, ...issueDtoToManifest(result, row.ClientId) }
      : c)
  } else {
    stagedCredentials.value = [
      ...stagedCredentials.value,
      issueDtoToManifest(result, result.ClientId?.trim() || newClientId()),
    ]
  }
}

function addCredential() {
  if (stagedSave.value) return openStagedCredential()
  return isCreate.value ? openInitialCredential() : openCredentialModal('create')
}

function removeStagedCredential(row: CredentialRow) {
  stagedCredentials.value = stagedCredentials.value.filter((c) => c.ClientId !== row.ClientId)
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
          <CoarButton v-if="!props.draftOnly" size="s" :icon-start="initialCredential && isCreate && !stagedSave ? 'pencil' : 'plus'"
            class="shrink-0" @click="addCredential">
            {{ initialCredential && isCreate && !stagedSave
              ? t('admin.serviceAccountCredentials.editInitialButton', {}, 'Edit OAuth client')
              : t('admin.serviceAccountCredentials.issueButton', {}, 'Add OAuth client') }}
          </CoarButton>
        </div>

        <CoarNotice v-if="stagedSave" truncate variant="info" class="mb-3">
          {{ t('admin.serviceAccountCredentials.stagedHint', {}, 'Credentials are staged with the account. A new one is issued at apply and its secret shown once in the apply result; a removed one is deleted at apply.') }}
        </CoarNotice>

        <div v-if="isCreate && initialCredential && !stagedSave" class="initial-credential">
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

        <div v-if="isCreate && !initialCredential && !props.draftOnly && !stagedSave" class="credential-empty">
          {{ t('admin.serviceAccountCredentials.initialEmpty', {}, 'No initial OAuth client configured yet.') }}
        </div>
        <div v-else-if="isCreate && props.draftOnly" class="credential-empty">
          {{ t('admin.serviceAccountCredentials.outerClientConfigured', {}, 'Scopes, apps, token settings, and the secret are configured in the parent OAuth client.') }}
        </div>
        <template v-if="!isCreate || stagedSave">
          <div v-if="credentialsLoading" class="text-xs text-surface-500">
            {{ t('common.loading', {}, 'Loading...') }}
          </div>
          <div v-else-if="credentialRows.length === 0" class="credential-empty">
            {{ t('admin.serviceAccountCredentials.empty', {}, 'No OAuth clients yet.') }}
          </div>
          <ul v-else class="flex flex-col gap-2">
            <li v-for="row in credentialRows" :key="row.key"
                class="flex flex-col gap-2 rounded border border-surface-200 p-3">
            <div class="flex flex-wrap items-baseline gap-2">
              <code class="text-sm font-medium">{{ row.ClientId }}</code>
              <span v-if="row.DisplayName" class="text-xs text-surface-500">— {{ row.DisplayName }}</span>
              <CoarTag v-if="!row.Enabled" variant="warning">
                {{ t('admin.serviceAccountCredentials.disabled', {}, 'Disabled') }}
              </CoarTag>
              <CoarTag v-if="stagedSave && !row.live" variant="info">
                {{ t('admin.serviceAccountCredentials.statusStaged', {}, 'Issued at apply') }}
              </CoarTag>
            </div>
            <div class="flex flex-wrap items-center gap-1.5 text-xs text-surface-500">
              <span v-if="row.Scopes.length > 0" class="flex flex-wrap gap-1">
                <CoarTag v-for="s in row.Scopes" :key="s">{{ s }}</CoarTag>
              </span>
              <span v-else>{{ t('admin.serviceAccountCredentials.noScopes', {}, 'No scopes set') }}</span>
              <span class="mx-1">·</span>
              <span>{{ row.AppCount }} {{ t('admin.serviceAccountCredentials.appsLinked', {}, 'app(s)') }}</span>
              <span v-if="row.AccessTokenLifetime != null" class="mx-1">·</span>
              <span v-if="row.AccessTokenLifetime != null">{{ row.AccessTokenLifetime }}s</span>
            </div>
            <div class="flex items-center justify-end gap-1">
              <CoarButton size="s" variant="ghost" icon-start="pencil"
                @click="stagedSave ? openStagedCredential(row) : openCredentialModal(row.Id!)">
                {{ t('common.edit', {}, 'Edit') }}
              </CoarButton>
              <!-- Rotation is credential MATERIAL — live in every mode, and only for a client that exists. -->
              <CoarPopconfirm
                v-if="row.live"
                :title="t('admin.serviceAccountCredentials.rotateTitle', {}, 'Rotate secret?')"
                :message="t('admin.serviceAccountCredentials.rotateConfirm', {}, 'The old secret stops working immediately and the new one is shown only once.')"
                @confirmed="rotateCredential(row.live!)">
                <CoarButton size="s" variant="ghost" icon-start="rotate-ccw">
                  {{ t('admin.serviceAccountCredentials.rotateButton', {}, 'Rotate') }}
                </CoarButton>
              </CoarPopconfirm>
              <CoarPopconfirm
                :title="t('admin.serviceAccountCredentials.deleteTitle', {}, 'Delete OAuth client?')"
                :message="stagedSave
                  ? t('admin.serviceAccountCredentials.deleteStagedConfirm', {}, 'Removed from the draft — deleted when the draft is applied; the plan shows it.')
                  : t('admin.serviceAccountCredentials.deleteConfirm', {}, 'Existing tokens stay valid until expiry but no new tokens can be minted.')"
                confirm-variant="danger"
                @confirmed="stagedSave ? removeStagedCredential(row) : deleteCredential(row.live!)">
                <CoarButton size="s" variant="ghost" icon-start="trash-2">
                  {{ stagedSave ? t('common.remove', {}, 'Remove') : t('common.delete', {}, 'Delete') }}
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
