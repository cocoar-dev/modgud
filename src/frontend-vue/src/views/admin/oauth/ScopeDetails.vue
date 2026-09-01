<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarCheckbox,
  CoarDivider,
  CoarFormField,
  CoarNotice,
  CoarSelect,
  CoarTab,
  CoarTabGroup,
  CoarTextInput,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import EditableStringList from '@/components/EditableStringList.vue'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useClone, SCOPE_CLONE } from '@/composables/useClone'
import { useDraftStaging } from '@/composables/useDraftStaging'
import type { ManifestEntity } from '@/stores/realmDraft.store'
import type { OAuthScopeDto } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const appContextStore = useAppContextStore()
const { consume } = useClone()
const isCreate = computed(() => props.id === 'create')

// ── ADR-0005 staging: scope saves commit onto the active draft. The scope
// name is the immutable natural key, so edits stage under it directly.
const staging = useDraftStaging('scopes')
const isDraftRow = computed(() => staging.isDraftId(props.id))
const stagedSave = computed(() => staging.stagingActive.value && !isStandard.value)

function appSlugOf(appId: string): string | undefined {
  if (!appId) return undefined
  return applicationsStore.apps.find((a) => a.Id === appId)?.Slug
}

function appIdOf(slug: unknown): string {
  if (typeof slug !== 'string' || !slug) return ''
  return applicationsStore.apps.find((a) => a.Slug === slug)?.Id ?? ''
}

function fromStaged(e: ManifestEntity): FormState {
  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  const arr = (v: unknown) => (Array.isArray(v) ? [...(v as string[])] : [])
  return {
    Name: str(e.Name),
    DisplayName: str(e.DisplayName),
    Description: str(e.Description),
    Resources: arr(e.Resources),
    UserClaims: arr(e.UserClaims),
    Enabled: e.Enabled !== false,
    Required: e.Required === true,
    Emphasize: e.Emphasize === true,
    ShowInDiscoveryDocument: e.ShowInDiscoveryDocument !== false,
    AppId: appIdOf(e.App),
    AllowDynamicRegistrationClients: e.AllowDynamicRegistrationClients === true,
  }
}

function toStaged(): ManifestEntity {
  const entity: ManifestEntity = {
    Name: form.value.Name.trim(),
    Resources: [...form.value.Resources],
    UserClaims: [...form.value.UserClaims],
    Enabled: form.value.Enabled,
    Required: form.value.Required,
    Emphasize: form.value.Emphasize,
    ShowInDiscoveryDocument: form.value.ShowInDiscoveryDocument,
    AllowDynamicRegistrationClients: form.value.AllowDynamicRegistrationClients,
  }
  if (form.value.DisplayName.trim()) entity.DisplayName = form.value.DisplayName.trim()
  if (form.value.Description.trim()) entity.Description = form.value.Description.trim()
  const slug = appSlugOf(form.value.AppId)
  if (slug) entity.App = slug
  return entity
}

// Empty value = "global" (cross-app, e.g. standard OIDC scopes).
const appOptions = computed(() => [
  { value: '', label: t('admin.oauthScopes.app.global', {}, '— Realm-wide (cross-app)') },
  ...applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: `${a.DisplayName} (${a.Slug})`,
  })),
])
const loading = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)
const dto = ref<OAuthScopeDto | null>(null)
const activeTab = ref<'general' | 'content' | 'behavior'>('general')
const isStandard = computed(() => dto.value?.IsStandard === true)

interface FormState {
  Name: string
  DisplayName: string
  Description: string
  Resources: string[]
  UserClaims: string[]
  Enabled: boolean
  Required: boolean
  Emphasize: boolean
  ShowInDiscoveryDocument: boolean
  /** Empty string = "global", otherwise an App.Id. */
  AppId: string
  AllowDynamicRegistrationClients: boolean
}

function emptyForm(): FormState {
  return {
    Name: '',
    DisplayName: '',
    Description: '',
    Resources: [],
    UserClaims: [],
    Enabled: true,
    Required: false,
    Emphasize: false,
    ShowInDiscoveryDocument: true,
    AppId: '',
    AllowDynamicRegistrationClients: false,
  }
}

const form = ref<FormState>(emptyForm())

function fromDto(dto: OAuthScopeDto): FormState {
  return {
    Name: dto.Name,
    DisplayName: dto.DisplayName ?? '',
    Description: dto.Description ?? '',
    Resources: [...(dto.Resources ?? [])],
    UserClaims: [...(dto.UserClaims ?? [])],
    Enabled: dto.Enabled,
    Required: dto.Required,
    Emphasize: dto.Emphasize,
    ShowInDiscoveryDocument: dto.ShowInDiscoveryDocument,
    AppId: dto.AppId ?? '',
    AllowDynamicRegistrationClients: dto.AllowDynamicRegistrationClients,
  }
}

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.oauthScopes.createTitle', {}, 'Create Scope')
    : (form.value.DisplayName || form.value.Name)
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Name)

const footerButton = computed(() => ({
  visible: true,
  text: stagedSave.value
    ? t('admin.realmConfig.entry.save', {}, 'In den Draft übernehmen')
    : isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim() || saving.value,
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
    // Clone: prefill the form with the Name (immutable) blanked.
    const clone = consume<OAuthScopeDto>(SCOPE_CLONE.entity)
    if (clone) {
      form.value = fromDto(clone)
    } else {
      // A scope created from a filtered App workspace should stay visible in
      // that workspace instead of silently becoming realm-wide and vanishing
      // from the grid after Save.
      form.value.AppId = appContextStore.selectedAppId ?? ''
    }
    return
  }
  loading.value = true
  try {
    const loaded = await store.loadOne(props.id)
    if (!loaded) {
      error.value = t('admin.oauthScopes.loadFailed', {}, 'Failed to load the scope.')
      return
    }
    dto.value = loaded
    form.value = fromDto(loaded)
    // Staging overlay: show the STAGED scope state when the draft carries it.
    if (stagedSave.value && staging.draftStore.current) {
      const entity = staging.findStaged(loaded.Name)
      if (entity) form.value = fromStaged(entity)
    }
  } finally {
    loading.value = false
  }
})

async function save() {
  if (!form.value.Name.trim() || isStandard.value) return
  saving.value = true
  error.value = null
  try {
    // ADR-0005: commit onto the active draft instead of writing live.
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
        Resources: [...form.value.Resources],
        UserClaims: [...form.value.UserClaims],
        Enabled: form.value.Enabled,
        Required: form.value.Required,
        Emphasize: form.value.Emphasize,
        ShowInDiscoveryDocument: form.value.ShowInDiscoveryDocument,
        AppId: form.value.AppId || null,
        AllowDynamicRegistrationClients: form.value.AllowDynamicRegistrationClients,
      })
      props.close(created)
    } else {
      await store.update(props.id, {
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Resources: [...form.value.Resources],
        UserClaims: [...form.value.UserClaims],
        Enabled: form.value.Enabled,
        Required: form.value.Required,
        Emphasize: form.value.Emphasize,
        ShowInDiscoveryDocument: form.value.ShowInDiscoveryDocument,
        // Always send — empty string = make global, guid = assign.
        AppId: form.value.AppId,
        AllowDynamicRegistrationClients: form.value.AllowDynamicRegistrationClients,
      })
      props.close()
    }
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.Message ?? e?.body?.error ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="tags"
    :footer-button="footerButton" :readonly="isStandard">
    <template #banner>
      <CoarNotice v-if="isCreate" placement="banner" variant="info"
        :label="t('admin.oauthScopes.createBannerLabel', {}, 'New scope')">
        {{ t('admin.oauthScopes.createBanner', {}, 'The scope name is the protocol identifier clients request and cannot be changed later.') }}
      </CoarNotice>
      <CoarNotice v-else-if="isStandard" placement="banner" variant="info"
        :label="t('common.systemManagedLabel', {}, 'System')">
        {{ t('admin.oauthScopes.standardManaged', {}, 'This standard OIDC scope is managed by the IdP and cannot be changed.') }}
      </CoarNotice>
    </template>

    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
    <div v-else class="scope-editor">
      <CoarNotice v-if="error" variant="error" class="scope-error">
        {{ error }}
      </CoarNotice>
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">{{ t('admin.oauthScopes.tabs.general', {}, 'General') }}</CoarTab>
        <CoarTab id="content">{{ t('admin.oauthScopes.tabs.content', {}, 'Token content') }}</CoarTab>
        <CoarTab id="behavior">{{ t('admin.oauthScopes.tabs.behavior', {}, 'Behavior') }}</CoarTab>
      </CoarTabGroup>

      <!-- General: stable identity and App ownership. -->
      <div v-show="activeTab === 'general'" class="tab-content">
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">
              {{ t('admin.oauthScopes.section.identity', {}, 'Identity') }}
            </h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-half" :label="t('admin.oauthScopes.name', {}, 'Scope name')"
              :required="isCreate"
              :hint="t('admin.oauthScopes.name.hint', {}, 'Technical name in the OAuth protocol.\n\nExample: acme.read\nImmutable after creation.')">
              <CoarTextInput v-if="isCreate" v-model="form.Name" clearable
                :placeholder="t('admin.oauthScopes.name.placeholder', {}, 'acme.read')" />
              <div v-else class="scope-name-readonly">
                <code>{{ form.Name }}</code>
              </div>
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.oauthScopes.displayName', {}, 'Display name')"
              :hint="t('admin.oauthScopes.displayName.hint', {}, 'Human-readable name shown on the consent screen.')">
              <CoarTextInput v-model="form.DisplayName" :disabled="isStandard" clearable />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.oauthScopes.description', {}, 'Description')"
              :hint="t('admin.oauthScopes.description.hint', {}, 'Optional explanation shown to users on the consent screen.')">
              <CoarTextInput v-model="form.Description" :disabled="isStandard" clearable :rows="3" />
            </CoarFormField>
          </div>
        </section>

        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">
              {{ t('admin.oauthScopes.section.assignment', {}, 'Scope assignment') }}
            </h3>
          </CoarDivider>
          <CoarFormField class="app-field" :label="t('admin.oauthScopes.app', {}, 'Application')"
            :hint="form.AppId
              ? t('admin.oauthScopes.app.scopedHint', {}, 'Only OAuth clients linked to this App may request this scope.')
              : t('admin.oauthScopes.app.globalHint', {}, 'Cross-app scope. Any client with this scope on its allow-list may request it.')">
            <CoarSelect v-model="form.AppId" :options="appOptions" :disabled="isStandard" />
          </CoarFormField>
        </section>
      </div>

      <!-- Token content: compact list editors instead of two 12rem empty
           technical grids stacked below one another. -->
      <div v-show="activeTab === 'content'" class="tab-content tab-content--lists">
        <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
          <h3 class="section-divider__title">
            {{ t('admin.oauthScopes.section.content', {}, 'Token and UserInfo content') }}
          </h3>
        </CoarDivider>
        <div class="content-grid">
          <EditableStringList
            v-model="form.Resources"
            appearance="compact-grid"
            fill-available
            :disabled="isStandard"
            :header-label="t('admin.oauthScopes.resources', {}, 'API audiences')"
            :header-hint="t('admin.oauthScopes.resources.hint', {}, 'aud values for which a token carrying this scope is valid. Bare identifiers and absolute URIs are supported.')"
            :placeholder="t('admin.oauthScopes.resource.placeholder', {}, 'acme-api')" />
          <EditableStringList
            v-model="form.UserClaims"
            appearance="compact-grid"
            fill-available
            :disabled="isStandard"
            :header-label="t('admin.oauthScopes.userClaims', {}, 'User claims')"
            :header-hint="t('admin.oauthScopes.userClaims.hint', {}, 'OIDC claim names added to the token or UserInfo when this scope is granted.')"
            :placeholder="t('admin.oauthScopes.userClaim.placeholder', {}, 'email')" />
        </div>
      </div>

      <!-- Behavior: compact policy switches. The section headings provide
           enough grouping; individual checkboxes do not need card chrome. -->
      <div v-show="activeTab === 'behavior'" class="tab-content">
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">
              {{ t('admin.oauthScopes.section.behavior', {}, 'Availability and consent') }}
            </h3>
          </CoarDivider>
          <div class="option-grid">
            <CoarFormField class="option-field" layout="inline" label-position="after"
              :label="t('admin.oauthScopes.enabled', {}, 'Active')"
              :hint="t('admin.oauthScopes.enabled.hint', {}, 'Only active scopes can be requested by clients.')">
              <CoarCheckbox v-model="form.Enabled" :disabled="isStandard" />
            </CoarFormField>
            <CoarFormField class="option-field" layout="inline" label-position="after"
              :label="t('admin.oauthScopes.required', {}, 'Required in consent')"
              :hint="t('admin.oauthScopes.required.hint', {}, 'Users cannot deselect this scope on the consent screen.')">
              <CoarCheckbox v-model="form.Required" :disabled="isStandard" />
            </CoarFormField>
            <CoarFormField class="option-field" layout="inline" label-position="after"
              :label="t('admin.oauthScopes.emphasize', {}, 'Emphasize in consent')"
              :hint="t('admin.oauthScopes.emphasize.hint', {}, 'Highlights the scope as security-relevant on the consent screen.')">
              <CoarCheckbox v-model="form.Emphasize" :disabled="isStandard" />
            </CoarFormField>
            <CoarFormField class="option-field" layout="inline" label-position="after"
              :label="t('admin.oauthScopes.showInDiscovery', {}, 'Publish in discovery')"
              :hint="t('admin.oauthScopes.showInDiscovery.hint', {}, 'Lists this scope in scopes_supported in the public OIDC discovery document.')">
              <CoarCheckbox v-model="form.ShowInDiscoveryDocument" :disabled="isStandard" />
            </CoarFormField>
          </div>
        </section>

        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">
              {{ t('admin.oauthScopes.allowDcr', {}, 'Dynamic Client Registration') }}
            </h3>
          </CoarDivider>
          <CoarFormField class="dcr-field" layout="inline" label-position="after"
            :label="t('admin.oauthScopes.allowDcr.toggle', {}, 'Allow dynamically registered clients')"
            :hint="t('admin.oauthScopes.allowDcr.hint', {}, 'DCR clients may request this scope only when the realm, target API and this scope all allow it.')">
            <CoarCheckbox v-model="form.AllowDynamicRegistrationClients" :disabled="isStandard" />
          </CoarFormField>
        </section>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
.scope-editor {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
  min-height: 0;
}

.scope-error,
.tab-bar {
  flex-shrink: 0;
}

.tab-content {
  flex: 1;
  min-width: 0;
  min-height: 0;
  overflow-y: auto;
  padding-bottom: 0.25rem;
}

.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.75rem;
  font-weight: 650;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.form-section + .form-section {
  margin-top: 1.25rem;
}

.scope-name-readonly {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-height: 2.5rem;
}

.scope-name-readonly code {
  flex: 1;
  overflow: hidden;
  padding: 0.45rem 0.6rem;
  border: 1px solid var(--coar-border-neutral-subtle, #d4d8e1);
  border-radius: var(--coar-radius-m, 4px);
  background: var(--coar-background-neutral-primary, #fff);
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.82rem;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.app-field {
  max-width: 28rem;
}

.content-grid {
  display: grid;
  flex: 1;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 1rem;
  min-height: 0;
}

.tab-content--lists {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.option-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.75rem;
}

.option-field,
.dcr-field {
  min-width: 0;
}

.dcr-field {
  width: 100%;
}

@media (max-width: 760px) {
  .content-grid,
  .option-grid {
    grid-template-columns: 1fr;
  }
}
</style>
