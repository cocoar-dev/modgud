<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarNotice, CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import ApiFormSections, {
  type ApiFormState,
  type ApiFormSection,
} from './ApiFormSections.vue'
import { useOAuthApiStore } from '@/stores/oauthApi.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useClone, API_CLONE } from '@/composables/useClone'
import { useDraftStaging } from '@/composables/useDraftStaging'
import type { ManifestEntity } from '@/stores/realmDraft.store'
import type { OAuthApiDto } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthApiStore()
const applicationsStore = useApplicationsStore()
const appContextStore = useAppContextStore()
const { consume } = useClone()
const isCreate = computed(() => props.id === 'create')

// ── ADR-0017 staging: API saves commit onto the active draft. Natural key =
// the immutable audience (Name); permission ids map to resource:action pairs
// via the linked app's catalog (and back).
const staging = useDraftStaging('apis')
const isDraftRow = computed(() => staging.isDraftId(props.id))
const stagedSave = computed(() => staging.stagingActive.value)

function appOf(appId: string) {
  return applicationsStore.apps.find((a) => a.Id === appId)
}

function fromStaged(e: ManifestEntity): ApiFormState {
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  const arr = (v: unknown) => (Array.isArray(v) ? [...(v as string[])] : [])
  const app = applicationsStore.apps.find((a) => a.Slug === str(e.App))
  const catalog = app?.Permissions ?? []
  const permissionIds = new Set<string>()
  for (const perm of Array.isArray(e.Permissions) ? (e.Permissions as ManifestEntity[]) : []) {
    const hit = catalog.find((c) => c.Resource === perm.Resource && c.Action === perm.Action)
    if (hit) permissionIds.add(hit.Id)
  }
  return {
    Name: str(e.Name),
    DisplayName: str(e.DisplayName),
    Description: str(e.Description),
    Scopes: arr(e.Scopes),
    UserClaims: arr(e.UserClaims),
    Enabled: e.Enabled !== false,
    AppId: app?.Id ?? '',
    PermissionIds: permissionIds,
    AllowDynamicRegistration: e.AllowDynamicRegistration === true,
  }
}

function toStaged(): ManifestEntity {
  const app = appOf(form.value.AppId)
  const entity: ManifestEntity = {
    Name: form.value.Name.trim(),
    Scopes: [...form.value.Scopes],
    UserClaims: [...form.value.UserClaims],
    Enabled: form.value.Enabled,
    AllowDynamicRegistration: form.value.AllowDynamicRegistration,
  }
  // v2 merge-patch: explicit null stages the clear (absent would keep live) —
  // App: null detaches the RS back to unassigned.
  entity.DisplayName = form.value.DisplayName.trim() || null
  entity.Description = form.value.Description.trim() || null
  entity.App = app?.Slug ?? null
  if (app) {
    entity.Permissions = (app.Permissions ?? [])
      .filter((c) => form.value.PermissionIds.has(c.Id))
      .map((c) => ({ Resource: c.Resource, Action: c.Action }))
  }
  // Stage the LIVE entity's id: the apply matches by identity, so editing the name
  // is a RENAME of this entity instead of staging a second one.
  if (!isCreate.value && !isDraftRow.value) entity.Id = props.id
  return entity
}

// Empty value = "unassigned" — RS exists but the IdP can't resolve a
// catalog for it, so UserInfo will not emit a resource_access block.
const appOptions = computed(() => [
  { value: '', label: t('admin.oauthApis.app.unassigned', {}, '— Unassigned (no UserInfo emission)') },
  ...applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: `${a.DisplayName} (${a.Slug})`,
  })),
])
const loading = ref(false)
const saving = ref(false)
const actionLoading = ref(false)
const error = ref<string | null>(null)
const busy = computed(() => saving.value || actionLoading.value)

function emptyForm(): ApiFormState {
  return {
    Name: '',
    DisplayName: '',
    Description: '',
    Scopes: [],
    UserClaims: [],
    Enabled: true,
    AppId: '',
    PermissionIds: new Set<string>(),
    AllowDynamicRegistration: false,
  }
}

const form = ref<ApiFormState>(emptyForm())
const dto = ref<OAuthApiDto | null>(null)

function fromDto(d: OAuthApiDto): ApiFormState {
  return {
    Name: d.Name,
    DisplayName: d.DisplayName ?? '',
    Description: d.Description ?? '',
    Scopes: [...(d.Scopes ?? [])],
    UserClaims: [...(d.UserClaims ?? [])],
    Enabled: d.Enabled,
    AppId: d.AppId ?? '',
    PermissionIds: new Set(d.PermissionIds ?? []),
    AllowDynamicRegistration: d.AllowDynamicRegistration,
  }
}

/** Catalog of the currently selected App, ordered for display. */
const linkedAppCatalog = computed(() => {
  if (!form.value.AppId) return []
  const app = applicationsStore.apps.find((a) => a.Id === form.value.AppId)
  if (!app) return []
  return [...(app.Permissions ?? [])].sort((a, b) =>
    `${a.Resource}:${a.Action}`.localeCompare(`${b.Resource}:${b.Action}`))
})

const tab = ref<ApiFormSection>('identity')

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.oauthApis.createTitle', {}, 'Create API')
    : (form.value.DisplayName || form.value.Name))
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Name)

const footerButton = computed(() => ({
  visible: true,
  text: stagedSave.value
    ? t('admin.realmConfig.entry.save', {}, 'In den Draft übernehmen')
    : isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim() || busy.value,
  loading: saving.value,
  onClick: save,
}))

onMounted(async () => {
  await applicationsStore.initialize()
  if (isDraftRow.value) {
    const entity = staging.findStaged(staging.draftKeyOf(props.id))
    if (entity) form.value = fromStaged(entity)
    return
  }
  if (isCreate.value) {
    // Clone: prefill the tabs with the immutable audience blanked. The linked
    // application and its catalog subset clone 1:1.
    const clone = consume<OAuthApiDto>(API_CLONE.entity)
    if (clone) {
      form.value = fromDto(clone)
    } else {
      form.value.AppId = appContextStore.selectedAppId ?? ''
    }
    return
  }
  loading.value = true
  try {
    const loaded = await store.loadOne(props.id)
    if (!loaded) {
      error.value = t('admin.oauthApis.loadFailed', {}, 'Failed to load the API.')
      return
    }
    dto.value = loaded
    form.value = fromDto(loaded)
    // Staging overlay: the draft is the working state when it carries this API.
    if (stagedSave.value && staging.draftStore.current) {
      const entity = staging.findStaged(loaded.Name)
      if (entity) form.value = fromStaged(entity)
    }
  } finally {
    loading.value = false
  }
})

async function save() {
  if (!form.value.Name.trim()) return
  saving.value = true
  error.value = null
  try {
    // ADR-0017: commit onto the active draft instead of writing live.
    if (stagedSave.value) {
      await staging.stage(form.value.Name.trim(), toStaged())
      props.close()
      return
    }
    if (isCreate.value) {
      const created = await store.create({
        Name: form.value.Name.trim(),
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Scopes: [...form.value.Scopes],
        UserClaims: [...form.value.UserClaims],
        Enabled: form.value.Enabled,
        AppId: form.value.AppId || null,
        PermissionIds: form.value.AppId ? Array.from(form.value.PermissionIds) : [],
        AllowDynamicRegistration: form.value.AllowDynamicRegistration,
      })
      props.close(created)
    } else {
      const updated = await store.update(props.id, {
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Scopes: [...form.value.Scopes],
        UserClaims: [...form.value.UserClaims],
        Enabled: form.value.Enabled,
        // Always send — v2 merge-patch: explicit null detaches, guid assigns.
        AppId: form.value.AppId || null,
        PermissionIds: form.value.AppId ? Array.from(form.value.PermissionIds) : [],
        AllowDynamicRegistration: form.value.AllowDynamicRegistration,
      })
      dto.value = updated
      props.close()
    }
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.Message ?? e?.body?.error ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

/** Mint the 1:1 OAuthScope companion for this API (edit only). */
async function createImplicitScope() {
  if (isCreate.value || !dto.value) return
  actionLoading.value = true
  error.value = null
  try {
    await store.createImplicitScope(dto.value.Id)
    const reloaded = await store.loadOne(dto.value.Id)
    if (reloaded) {
      dto.value = reloaded
      form.value = fromDto(reloaded)
    }
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.Message ?? e?.body?.error ?? e?.message ?? String(e)
  } finally {
    actionLoading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="server"
    :footer-button="footerButton">
    <template #banner>
      <CoarNotice v-if="isCreate" placement="banner" variant="info"
        :label="t('admin.oauthApis.createBannerLabel', {}, 'New API')">
        {{ t('admin.oauthApis.createBanner', {}, 'The audience identifies this resource server in issued tokens and cannot be changed later.') }}
      </CoarNotice>
    </template>

    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
    <div v-else class="api-modal-body">
      <CoarNotice v-if="error" variant="error" class="error-banner">{{ error }}</CoarNotice>

      <CoarTabGroup v-model="tab" class="tab-bar">
        <CoarTab id="identity">{{ t('admin.oauthApis.section.identity', {}, 'Identity') }}</CoarTab>
        <CoarTab id="linkage">{{ t('admin.oauthApis.section.linkage', {}, 'Linkage') }}</CoarTab>
        <CoarTab id="surface">{{ t('admin.oauthApis.section.surface', {}, 'OAuth surface') }}</CoarTab>
        <CoarTab id="options">{{ t('admin.oauthApis.section.options', {}, 'Options') }}</CoarTab>
      </CoarTabGroup>
      <div class="tab-body">
        <ApiFormSections :section="tab" :form="form" :is-create="isCreate"
          :app-options="appOptions" :linked-app-catalog="linkedAppCatalog" :dto="dto" :loading="busy"
          @create-implicit-scope="createImplicitScope" />
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.api-modal-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
  gap: 12px;
}
.error-banner {
  flex-shrink: 0;
}
.tab-bar {
  flex-shrink: 0;
}
.tab-body {
  display: flex;
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}
.tab-body > * {
  width: 100%;
}
</style>
