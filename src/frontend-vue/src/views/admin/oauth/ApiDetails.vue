<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTabGroup, CoarTab } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import WizardStepper, { type WizardStep } from '@/components/WizardStepper.vue'
import ApiFormSections, {
  type ApiFormState,
  type ApiFormSection,
} from './ApiFormSections.vue'
import { useOAuthApiStore } from '@/stores/oauthApi.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type { OAuthApiDto } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthApiStore()
const applicationsStore = useApplicationsStore()
const isCreate = computed(() => props.id === 'create')

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
const error = ref<string | null>(null)

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

// ── Create = wizard ──────────────────────────────────────────────────
const step = ref(0)
const wizardSteps = computed<WizardStep[]>(() => [
  { key: 'identity', title: t('admin.oauthApis.section.identity', {}, 'Identity'), valid: !!form.value.Name.trim() },
  { key: 'linkage', title: t('admin.oauthApis.section.linkage', {}, 'Linkage') },
  { key: 'config', title: t('admin.oauthApis.section.config', {}, 'OAuth & options') },
  { key: 'review', title: t('admin.oauthApis.section.review', {}, 'Review') },
])

// ── Edit = tabs ──────────────────────────────────────────────────────
const tab = ref<ApiFormSection>('identity')

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.oauthApis.createTitle', {}, 'Create API')
    : (form.value.DisplayName || form.value.Name))
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Name)

// Edit footer = single Save (ModalLayout footer). Create has no ModalLayout
// footer — the wizard owns its own Back/Next/Create nav.
const editFooterButton = computed(() => ({
  visible: true,
  text: t('common.save', {}, 'Save'),
  disabled: loading.value,
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  applicationsStore.initialize()
  if (isCreate.value) return
  loading.value = true
  try {
    const loaded = await store.loadOne(props.id)
    if (!loaded) {
      error.value = t('admin.oauthApis.loadFailed', {}, 'Failed to load the API.')
      return
    }
    dto.value = loaded
    form.value = fromDto(loaded)
  } finally {
    loading.value = false
  }
})

async function save() {
  if (!form.value.Name.trim()) return
  loading.value = true
  error.value = null
  try {
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
        // Always send — empty string detaches, guid assigns.
        AppId: form.value.AppId,
        PermissionIds: form.value.AppId ? Array.from(form.value.PermissionIds) : [],
        AllowDynamicRegistration: form.value.AllowDynamicRegistration,
      })
      dto.value = updated
      props.close()
    }
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

/** Mint the 1:1 OAuthScope companion for this API (edit only). */
async function createImplicitScope() {
  if (isCreate.value || !dto.value) return
  loading.value = true
  error.value = null
  try {
    await store.createImplicitScope(dto.value.Id)
    const reloaded = await store.loadOne(dto.value.Id)
    if (reloaded) {
      dto.value = reloaded
      form.value = fromDto(reloaded)
    }
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="server"
    :footer-button="isCreate ? undefined : editFooterButton">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
    <div v-else class="api-modal-body">
      <p v-if="error" class="error-banner">{{ error }}</p>

      <!-- CREATE: guided wizard -->
      <WizardStepper
        v-if="isCreate"
        v-model="step"
        :steps="wizardSteps"
        :submitting="loading"
        :finish-label="t('common.create', {}, 'Create')"
        @finish="save"
        @cancel="close()"
      >
        <template #step-identity>
          <ApiFormSections section="identity" :form="form" :is-create="true"
            :app-options="appOptions" :linked-app-catalog="linkedAppCatalog" :dto="dto" :loading="loading" />
        </template>
        <template #step-linkage>
          <ApiFormSections section="linkage" :form="form" :is-create="true"
            :app-options="appOptions" :linked-app-catalog="linkedAppCatalog" :dto="dto" :loading="loading" />
        </template>
        <template #step-config>
          <ApiFormSections section="surface" :form="form" :is-create="true"
            :app-options="appOptions" :linked-app-catalog="linkedAppCatalog" :dto="dto" :loading="loading" />
          <div class="step-section-gap"></div>
          <ApiFormSections section="options" :form="form" :is-create="true"
            :app-options="appOptions" :linked-app-catalog="linkedAppCatalog" :dto="dto" :loading="loading" />
        </template>
        <template #step-review>
          <ApiFormSections section="review" :form="form" :is-create="true"
            :app-options="appOptions" :linked-app-catalog="linkedAppCatalog" :dto="dto" :loading="loading" />
        </template>
      </WizardStepper>

      <!-- EDIT: tabs -->
      <template v-else>
        <CoarTabGroup v-model="tab" class="tab-bar">
          <CoarTab id="identity">{{ t('admin.oauthApis.section.identity', {}, 'Identity') }}</CoarTab>
          <CoarTab id="linkage">{{ t('admin.oauthApis.section.linkage', {}, 'Linkage') }}</CoarTab>
          <CoarTab id="surface">{{ t('admin.oauthApis.section.surface', {}, 'OAuth surface') }}</CoarTab>
          <CoarTab id="options">{{ t('admin.oauthApis.section.options', {}, 'Options') }}</CoarTab>
        </CoarTabGroup>
        <div class="tab-body">
          <ApiFormSections :section="tab" :form="form" :is-create="false"
            :app-options="appOptions" :linked-app-catalog="linkedAppCatalog" :dto="dto" :loading="loading"
            @create-implicit-scope="createImplicitScope" />
        </div>
      </template>
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
  font-size: 0.85rem;
  color: var(--coar-text-semantic-error, #b91c1c);
}
.tab-bar {
  flex-shrink: 0;
}
.tab-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}
.step-section-gap {
  height: 1rem;
}
</style>
