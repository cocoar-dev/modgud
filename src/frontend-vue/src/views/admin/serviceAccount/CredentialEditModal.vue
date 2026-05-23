<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarCheckbox,
  CoarNote,
  CoarDualListbox,
  CoarButton,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type {
  ServiceAccountCredentialIssuedDto,
  UpdateServiceAccountCredentialDto,
} from '@/models/serviceAccount'
import type { OAuthClientDto } from '@/models/oauth'

const { t } = useI18n()

// `id` is either `create` (issue new credential) or the credential's OAuth
// client Guid (edit existing). `saId` is the owning ServiceAccount id —
// always required since the route is nested under /credentials.
const props = defineProps<{
  saId: string
  id: string
  close: (result?: unknown) => void
}>()

const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

const scopeStore = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const credentialsHttp = computed(() => useHttpClient(`/api/service-account/${props.saId}/credentials`))

// Drives the cleartext-secret panel: present once after issue / rotate,
// then disappears when admin clicks "Done".
const newSecret = ref<string | null>(null)
const newClientIdForSecret = ref<string | null>(null)

const form = ref({
  DisplayName: '',
  Scopes: [] as string[],
  AppIds: [] as string[],
  AccessTokenLifetime: null as number | null,
  Enabled: true,
})
const originalForm = ref<typeof form.value | null>(null)

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
      group: isStandard ? 'Realm-wide (OIDC standard)' : `App: ${appLabel ?? '—'}`,
    }
  })
})

const appOptions = computed(() =>
  applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: a.DisplayName,
    subtitle: a.Description ?? a.Slug,
    icon: a.IsSystem ? 'shield' : 'layout-grid',
    group: a.IsSystem ? 'System apps' : 'User apps',
  })),
)

const modalTitle = computed(() => {
  if (newSecret.value)
    return t('admin.serviceAccountCredentials.secretTitle', {}, 'Credential issued')
  return isCreate.value
    ? t('admin.serviceAccountCredentials.issueTitle', {}, 'Issue credential')
    : (form.value.DisplayName || t('admin.serviceAccountCredentials.editTitle', {}, 'Edit credential'))
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
    text: isCreate.value ? t('common.issue', {}, 'Issue') : t('common.save', {}, 'Save'),
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
        error.value = t('admin.serviceAccountCredentials.notFound', {}, 'Credential not found.')
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
      const res = await credentialsHttp.value.post<ServiceAccountCredentialIssuedDto>({
        DisplayName: form.value.DisplayName.trim() || undefined,
        Scopes: form.value.Scopes,
        AppIds: form.value.AppIds,
        AccessTokenLifetime: form.value.AccessTokenLifetime ?? undefined,
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
      if (form.value.AccessTokenLifetime !== orig.AccessTokenLifetime)
        patch.AccessTokenLifetime = form.value.AccessTokenLifetime ?? undefined
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
  <ModalLayout :close="close" :title="modalTitle" icon="key" :footer-button="footerButton" width="56rem">
    <div v-if="loading && !newSecret" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>

    <!-- Secret-disclosure panel: shown once after Issue / Rotate. Modal
         footer flips to "Done" while this panel is visible. -->
    <div v-else-if="newSecret" class="flex flex-col gap-4 p-2">
      <CoarNote variant="warning">
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
      </CoarNote>
    </div>

    <div v-else class="flex flex-col gap-4 p-1">
      <CoarFormField :label="t('admin.serviceAccountCredentials.displayName', {}, 'Display name')">
        <CoarTextInput v-model="form.DisplayName" clearable
          :placeholder="t('admin.serviceAccountCredentials.displayNamePlaceholder', {}, 'CI build agent — staging')" />
      </CoarFormField>

      <div>
        <div class="mb-2 flex items-baseline justify-between gap-3">
          <h4 class="text-sm font-medium">{{ t('admin.serviceAccountCredentials.scopes', {}, 'Scopes') }}</h4>
          <p class="text-xs text-surface-500">
            {{ t('admin.serviceAccountCredentials.scopesHint', {}, 'Which scopes this credential is allowed to ask for. Realm-wide OIDC scopes are always available; per-API scopes need a matching App link below.') }}
          </p>
        </div>
        <CoarDualListbox
          v-model="form.Scopes"
          :options="scopeOptions"
          drag-drop
          sort-options="asc"
          :search-fields="['label', 'subtitle', 'group']"
          :available-label="t('admin.serviceAccountCredentials.scopesAvailable', {}, 'Available')"
          :selected-label="t('admin.serviceAccountCredentials.scopesSelected', {}, 'Allowed')"
          :search-placeholder="t('admin.serviceAccountCredentials.scopesSearch', {}, 'Search scopes…')"
          class="min-h-[14rem]" />
      </div>

      <div>
        <div class="mb-2 flex items-baseline justify-between gap-3">
          <h4 class="text-sm font-medium">{{ t('admin.serviceAccountCredentials.apps', {}, 'Apps') }}</h4>
          <p class="text-xs text-surface-500">
            {{ t('admin.serviceAccountCredentials.appsHint', {}, 'Apps this credential is allowed to act for. Empty = realm-wide. Use multiple when the M2M backend talks to several APIs.') }}
          </p>
        </div>
        <CoarDualListbox
          v-model="form.AppIds"
          :options="appOptions"
          drag-drop
          sort-options="asc"
          :search-fields="['label', 'subtitle', 'group']"
          :available-label="t('admin.serviceAccountCredentials.appsAvailable', {}, 'Available')"
          :selected-label="t('admin.serviceAccountCredentials.appsSelected', {}, 'Linked')"
          :search-placeholder="t('admin.serviceAccountCredentials.appsSearch', {}, 'Search apps…')"
          class="min-h-[10rem]" />
      </div>

      <CoarFormField :label="t('admin.serviceAccountCredentials.accessTokenLifetime', {}, 'Access-Token-Lebenszeit (Sekunden)')">
        <CoarTextInput v-model="accessTokenLifetimeText"
          :placeholder="t('admin.serviceAccountCredentials.accessTokenLifetimePlaceholder', {}, '3600 (Default)')" />
      </CoarFormField>

      <div v-if="!isCreate" class="mt-1">
        <CoarCheckbox v-model="form.Enabled" :label="t('admin.serviceAccountCredentials.enabled', {}, 'Aktiv')" />
      </div>

      <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
    </div>
  </ModalLayout>
</template>
