<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarNotice,
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarDualListbox,
  CoarButton,
  CoarSelect,
  CoarTabGroup,
  CoarTab,
  CoarDivider,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type {
  ServiceAccountCredentialIssuedDto,
  IssueServiceAccountCredentialDto,
  UpdateServiceAccountCredentialDto,
} from '@/models/serviceAccount'
import type { OAuthClientDto, AccessTokenType } from '@/models/oauth'

const { t } = useI18n()

// `id` is either `create` (issue new credential) or the credential's OAuth
// client Guid (edit existing). `saId` is the owning ServiceAccount id —
// always required since the route is nested under /credentials.
const props = defineProps<{
  saId: string
  id: string
  close: (result?: unknown) => void
  draftOnly?: boolean
  initial?: IssueServiceAccountCredentialDto
}>()

const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

// Same tabs in create and edit (Rule 5) — the Status tab is the only one that
// appears on an existing credential. The panel size is a fixed frame, so
// switching tabs never resizes the modal.
const activeTab = ref<'basics' | 'scopes' | 'apps'>('basics')

const scopeStore = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const credentialsHttp = computed(() => useHttpClient(`/api/service-account/${props.saId}/credentials`))

// Drives the cleartext-secret panel: present once after issue / rotate,
// then disappears when admin clicks "Done".
const newSecret = ref<string | null>(null)
const newClientIdForSecret = ref<string | null>(null)

const form = ref({
  DisplayName: props.initial?.DisplayName ?? '',
  Scopes: props.initial?.Scopes?.slice() ?? [] as string[],
  AppIds: props.initial?.AppIds?.slice() ?? [] as string[],
  AccessTokenLifetime: props.initial?.AccessTokenLifetime ?? null as number | null,
  // Default Reference — opaque + instantly revocable, so deactivate/delete/rotate
  // cuts off live M2M access immediately (Audit #6/#7/#8). JWT is opt-in.
  AccessTokenType: props.initial?.AccessTokenType ?? 'Reference' as AccessTokenType,
  Enabled: props.initial?.Enabled ?? true,
})
const originalForm = ref<typeof form.value | null>(null)

const accessTokenTypeOptions = computed<{ value: AccessTokenType; label: string }[]>(() => [
  { value: 'Reference', label: t('admin.serviceAccountCredentials.accessTokenTypeReference', {}, 'Reference (opaque, revocable instantly)') },
  { value: 'Jwt', label: t('admin.serviceAccountCredentials.accessTokenTypeJwt', {}, 'JWT (self-validating, revoke only takes effect on expiry)') },
])

// Text-backed proxy for the optional numeric AccessTokenLifetime — empty
// string maps back to null. CoarTextInput is text-only; the alternative
// would be coercing in the model getter/setter, but a computed is
// straightforward and keeps the form state numeric.
const accessTokenLifetimeText = computed({
  get: () => form.value.AccessTokenLifetime == null ? '' : String(form.value.AccessTokenLifetime),
  set: (v: string) => {
    const trimmed = v.trim()
    if (trimmed === '') { form.value.AccessTokenLifetime = null; return }
    const parsed = Number(trimmed)
    form.value.AccessTokenLifetime = Number.isFinite(parsed) ? parsed : null
  },
})

const scopeOptions = computed(() => {
  const standardOidc = new Set(['openid', 'profile', 'email', 'roles', 'offline_access', 'permissions'])
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
      group: isStandard
        ? t('admin.serviceAccountCredentials.scopeGroupRealmWide', {}, 'Realm-wide (OIDC standard)')
        : t('admin.serviceAccountCredentials.scopeGroupApp', { app: appLabel ?? '—' }, `App: ${appLabel ?? '—'}`),
    }
  })
})

const appOptions = computed(() =>
  applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: a.DisplayName,
    subtitle: a.Description ?? a.Slug,
    icon: a.IsSystem ? 'shield' : 'layout-grid',
    group: a.IsSystem
      ? t('admin.serviceAccountCredentials.appGroupSystem', {}, 'System apps')
      : t('admin.serviceAccountCredentials.appGroupUser', {}, 'User apps'),
  })),
)

const modalTitle = computed(() => {
  if (newSecret.value)
    return t('admin.serviceAccountCredentials.secretTitle', {}, 'OAuth client created')
  return isCreate.value
    ? t('admin.serviceAccountCredentials.issueTitle', {}, 'Add OAuth client')
    : (form.value.DisplayName || t('admin.serviceAccountCredentials.editTitle', {}, 'Edit OAuth client'))
})

const footerButton = computed(() => {
  if (newSecret.value) {
    return {
      visible: true,
      text: t('common.done', {}, 'Done'),
      disabled: false,
      onClick: () => props.close(true),
    }
  }
  return {
    visible: true,
    text: props.draftOnly
      ? t('admin.oauthClients.newServiceAccount.apply', {}, 'Übernehmen')
      : isCreate.value
        ? t('common.issue', {}, 'Issue')
        : t('common.save', {}, 'Save'),
    disabled: loading.value,
    onClick: save,
  }
})

onMounted(async () => {
  // Pre-load reference data so the dual-listboxes don't show empty
  // panels on first paint. Parallel for speed.
  await Promise.all([
    scopeStore.scopes.length === 0 ? scopeStore.loadAll() : Promise.resolve(),
    applicationsStore.apps.length === 0 ? applicationsStore.loadAll() : Promise.resolve(),
  ])

  if (!isCreate.value) {
    loading.value = true
    try {
      const list = await credentialsHttp.value.get<OAuthClientDto[]>()
      const cred = list.find((c) => c.Id === props.id)
      if (!cred) {
        error.value = t('admin.serviceAccountCredentials.notFound', {}, 'OAuth client not found.')
        return
      }
      // Map back from OAuthClientDto. AppIds on the server-side dto come as
      // ShortGuids but the apps-store keys them by Guid — leave as-is, the
      // listbox compares by string.
      form.value = {
        DisplayName: cred.DisplayName ?? '',
        Scopes: cred.Permissions
          .filter((p) => p.startsWith('scp:'))
          .map((p) => p.slice('scp:'.length)),
        AppIds: cred.AppIds.slice(),
        AccessTokenLifetime: cred.AccessTokenLifetime ?? null,
        AccessTokenType: (cred.AccessTokenType as AccessTokenType) ?? 'Reference',
        Enabled: cred.Enabled,
      }
      originalForm.value = JSON.parse(JSON.stringify(form.value))
    } catch (e: unknown) {
      const err = e as { data?: { Message?: string }; message?: string }
      error.value = err?.data?.Message ?? err?.message ?? String(e)
    } finally {
      loading.value = false
    }
  }
})

async function save() {
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      if (props.draftOnly) {
        props.close({
          DisplayName: form.value.DisplayName.trim() || undefined,
          Scopes: form.value.Scopes,
          AppIds: form.value.AppIds,
          AccessTokenLifetime: form.value.AccessTokenLifetime ?? undefined,
          AccessTokenType: form.value.AccessTokenType,
          Enabled: form.value.Enabled,
        } satisfies IssueServiceAccountCredentialDto)
        return
      }
      const res = await credentialsHttp.value.post<ServiceAccountCredentialIssuedDto>({
        DisplayName: form.value.DisplayName.trim() || undefined,
        Scopes: form.value.Scopes,
        AppIds: form.value.AppIds,
        AccessTokenLifetime: form.value.AccessTokenLifetime ?? undefined,
        AccessTokenType: form.value.AccessTokenType,
        Enabled: form.value.Enabled,
      })
      newSecret.value = res.ClientSecret
      newClientIdForSecret.value = res.Credential.ClientId
    } else {
      // Only patch what actually changed — empty patch is a no-op.
      const patch: UpdateServiceAccountCredentialDto = {}
      const orig = originalForm.value!
      if (form.value.DisplayName !== orig.DisplayName)
        patch.DisplayName = form.value.DisplayName
      if (!arraysEqual(form.value.Scopes, orig.Scopes))
        patch.Scopes = form.value.Scopes
      if (!arraysEqual(form.value.AppIds, orig.AppIds))
        patch.AppIds = form.value.AppIds
      // v2 merge-patch: an emptied lifetime sends explicit null (clear the
      // override back to the server default) — omitting it would mean "keep".
      if (form.value.AccessTokenLifetime !== orig.AccessTokenLifetime)
        patch.AccessTokenLifetime = form.value.AccessTokenLifetime
      if (form.value.AccessTokenType !== orig.AccessTokenType)
        patch.AccessTokenType = form.value.AccessTokenType
      if (form.value.Enabled !== orig.Enabled)
        patch.Enabled = form.value.Enabled

      await credentialsHttp.value.addPath(props.id).put<OAuthClientDto>(patch)
      props.close(true)
    }
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    error.value = err?.data?.Message ?? err?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

function arraysEqual(a: string[], b: string[]) {
  if (a.length !== b.length) return false
  const sa = a.slice().sort()
  const sb = b.slice().sort()
  return sa.every((v, i) => v === sb[i])
}

async function copySecret() {
  if (!newSecret.value) return
  await navigator.clipboard.writeText(newSecret.value)
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="key" :footer-button="footerButton">
    <div v-if="loading && !newSecret" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>

    <!-- Secret-disclosure panel: shown once after Issue / Rotate. Modal
         footer flips to "Done" while this panel is visible. -->
    <div v-else-if="newSecret" class="flex flex-col gap-4 p-2">
      <CoarNotice variant="warning">
        <div class="flex flex-col gap-3">
          <div class="font-medium">
            {{ t('admin.serviceAccountCredentials.secretOnce', {}, 'Copy the client secret now — it will not be shown again.') }}
          </div>
          <div class="flex flex-col gap-1">
            <span class="text-xs uppercase tracking-wide text-surface-500">
              {{ t('admin.serviceAccountCredentials.clientId', {}, 'Client ID') }}
            </span>
            <code class="text-sm">{{ newClientIdForSecret }}</code>
          </div>
          <div class="flex items-center gap-2">
            <code class="flex-1 break-all text-sm">{{ newSecret }}</code>
            <CoarButton size="s" icon-start="copy" @click="copySecret">
              {{ t('common.copy', {}, 'Copy') }}
            </CoarButton>
          </div>
        </div>
      </CoarNotice>
    </div>

    <div v-else class="modal-body">
      <CoarNotice v-if="error" variant="error" class="flex-shrink-0">{{ error }}</CoarNotice>

      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="basics">{{ t('admin.serviceAccountCredentials.section.basics', {}, 'Basics') }}</CoarTab>
        <CoarTab id="scopes">{{ t('admin.serviceAccountCredentials.scopes', {}, 'Scopes') }}</CoarTab>
        <CoarTab id="apps">{{ t('admin.serviceAccountCredentials.apps', {}, 'Apps') }}</CoarTab>
      </CoarTabGroup>

      <div v-show="activeTab === 'basics'" class="tab-content">
        <div class="modal-form">
          <section class="form-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h3 class="section-divider__title">
                {{ t('admin.serviceAccountCredentials.section.basics', {}, 'Basis') }}
              </h3>
            </CoarDivider>
            <div class="modal-form-grid">
              <CoarFormField class="col-half" :label="t('admin.serviceAccountCredentials.displayName', {}, 'Display name')"
                :hint="t('admin.serviceAccountCredentials.displayNameHint', {}, 'Free text so you can tell this OAuth client apart from the others of this service account.')">
                <CoarTextInput v-model="form.DisplayName" clearable
                  :placeholder="t('admin.serviceAccountCredentials.displayNamePlaceholder', {}, 'CI build agent — staging')" />
              </CoarFormField>

              <!-- No .field-num cap here: the width cap applies to the whole
                   field block, so it would wrap this long label over 3 lines. -->
              <CoarFormField class="col-half" :label="t('admin.serviceAccountCredentials.accessTokenLifetime', {}, 'Access-token lifetime (seconds)')"
                :hint="t('admin.serviceAccountCredentials.accessTokenLifetimeHint', {}, 'Empty = the realm default (3600 s). Keep JWT access tokens short-lived — a JWT stays valid until it expires, even after a revoke.')">
                <CoarTextInput v-model="accessTokenLifetimeText"
                  :placeholder="t('admin.serviceAccountCredentials.accessTokenLifetimePlaceholder', {}, '3600 (default)')" />
              </CoarFormField>

              <CoarFormField class="col-half" :label="t('admin.serviceAccountCredentials.accessTokenType', {}, 'Access-token format')"
                :hint="t('admin.serviceAccountCredentials.accessTokenTypeHint', {}, 'Reference tokens are revoked immediately (disabling/deleting/rotating takes effect right away); the resource server must introspect. A JWT validates itself but survives a revoke until it expires — keep the lifetime short in that case.')">
                <CoarSelect v-model="form.AccessTokenType" :options="accessTokenTypeOptions" />
              </CoarFormField>
            </div>
          </section>

          <section class="form-section">
            <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
              <h3 class="section-divider__title">
                {{ t('admin.serviceAccountCredentials.section.status', {}, 'Status') }}
              </h3>
            </CoarDivider>
            <div class="modal-form-grid">
              <CoarFormField class="col-full"
                :label="t('admin.serviceAccountCredentials.enabled', {}, 'Aktiv')"
                :hint="t('admin.serviceAccountCredentials.enabledHint', {}, 'An inactive OAuth client can no longer request tokens — issued tokens stay valid until they expire.')"
                layout="inline"
                label-position="after">
                <CoarCheckbox v-model="form.Enabled" />
              </CoarFormField>
            </div>
          </section>
        </div>
      </div>

      <div v-show="activeTab === 'scopes'" class="tab-content">
        <CoarNotice truncate variant="info">
          {{ t('admin.serviceAccountCredentials.scopesHintShort', {}, 'Which scopes this OAuth client may request.') }}
          <template #details>
            {{ t('admin.serviceAccountCredentials.scopesHint', {}, 'Which scopes this OAuth client may request. Realm-wide OIDC scopes are always available; per-API scopes need a matching App link on the Apps tab.') }}
          </template>
        </CoarNotice>
        <!-- .flex-section gives the listbox a definite height, so both tabs'
             lists are the same size whether they hold 0 or 50 entries. -->
        <section class="flex-section">
          <CoarDualListbox
            class="flex-1 min-h-0"
            v-model="form.Scopes"
            :options="scopeOptions"
            drag-drop
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :available-label="t('admin.serviceAccountCredentials.scopesAvailable', {}, 'Available')"
            :selected-label="t('admin.serviceAccountCredentials.scopesSelected', {}, 'Allowed')"
            :search-placeholder="t('admin.serviceAccountCredentials.scopesSearch', {}, 'Search scopes…')" />
        </section>
      </div>

      <div v-show="activeTab === 'apps'" class="tab-content">
        <CoarNotice truncate variant="info">
          {{ t('admin.serviceAccountCredentials.appsHintShort', {}, 'Empty = realm-wide.') }}
          <template #details>
            {{ t('admin.serviceAccountCredentials.appsHint', {}, 'Apps this OAuth client may act for. Empty = realm-wide. Use multiple when the M2M backend talks to several APIs.') }}
          </template>
        </CoarNotice>
        <section class="flex-section">
          <CoarDualListbox
            class="flex-1 min-h-0"
            v-model="form.AppIds"
            :options="appOptions"
            drag-drop
            sort-options="asc"
            :search-fields="['label', 'subtitle', 'group']"
            :available-label="t('admin.serviceAccountCredentials.appsAvailable', {}, 'Available')"
            :selected-label="t('admin.serviceAccountCredentials.appsSelected', {}, 'Linked')"
            :search-placeholder="t('admin.serviceAccountCredentials.appsSearch', {}, 'Search apps…')" />
        </section>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
/* Body-level layout — flex column so the tab bar stays put and the active
   tab body takes the rest of the fixed panel height. */
.modal-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
  gap: 12px;
}
.tab-bar {
  flex-shrink: 0;
}
.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
  overflow-y: auto;
}
/* Definite height for the dual-listboxes: the lists keep the same size
   whether they are empty or full, on both tabs. */
.flex-section {
  flex: 1;
  display: flex;
  min-height: 22rem;
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
</style>
