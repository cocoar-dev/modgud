<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarSelect } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import EditableStringList from '@/components/EditableStringList.vue'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useClone, SCOPE_CLONE } from '@/composables/useClone'
import type { OAuthScopeDto } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
const { consume } = useClone()
const isCreate = computed(() => props.id === 'create')

// Empty value = "global" (cross-app, e.g. standard OIDC scopes).
const appOptions = computed(() => [
  { value: '', label: t('admin.oauthScopes.app.global', {}, '— Global (cross-app, OIDC standard)') },
  ...applicationsStore.apps.map((a) => ({
    value: a.Id,
    label: `${a.DisplayName} (${a.Slug})`,
  })),
])
const loading = ref(false)
const error = ref<string | null>(null)

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
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.Name.trim() || loading.value,
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  applicationsStore.initialize()
  if (isCreate.value) {
    // Clone: prefill the form with the Name (immutable) blanked.
    const clone = consume<OAuthScopeDto>(SCOPE_CLONE.entity)
    if (clone) form.value = fromDto(clone)
    return
  }
  loading.value = true
  try {
    const dto = await store.loadOne(props.id)
    if (!dto) {
      error.value = t('admin.oauthScopes.loadFailed', {}, 'Failed to load the scope.')
      return
    }
    form.value = fromDto(dto)
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
      await store.create({
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
    }
    props.close()
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="tags"
    :footer-button="footerButton">
    <div v-if="loading && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1">
      <div class="modal-form">
        <!-- Section: Identity -->
        <section class="form-section">
          <h3 class="form-section-heading">{{ t('admin.oauthScopes.section.identity', {}, 'Identity') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-half" :label="t('admin.oauthScopes.name', {}, 'Name')" required
              :hint="t('admin.oauthScopes.name.hint', {}, 'Machine identifier clients send in the scope parameter (e.g. read:events). Immutable after creation.')">
              <CoarTextInput v-model="form.Name" :disabled="!isCreate" clearable />
            </CoarFormField>
            <CoarFormField class="col-half" :label="t('admin.oauthScopes.displayName', {}, 'Display Name')"
              :hint="t('admin.oauthScopes.displayName.hint', {}, 'Human-readable name shown on the consent screen.')">
              <CoarTextInput v-model="form.DisplayName" clearable />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.oauthScopes.description', {}, 'Description')"
              :hint="t('admin.oauthScopes.description.hint', {}, 'Optional explanation shown to users on the consent screen.')">
              <CoarTextInput v-model="form.Description" clearable :rows="2" />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Target & content -->
        <section class="form-section">
          <h3 class="form-section-heading">{{ t('admin.oauthScopes.section.target', {}, 'Target & content') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-half" :label="t('admin.oauthScopes.app', {}, 'Application')"
              :hint="form.AppId
                ? t('admin.oauthScopes.app.scopedHint', {}, 'Only OAuth clients linked to this App may request this scope.')
                : t('admin.oauthScopes.app.globalHint', {}, 'Cross-app scope (e.g. standard OIDC scopes). Any client may request it.')">
              <CoarSelect v-model="form.AppId" :options="appOptions" />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.oauthScopes.resources', {}, 'Resources (API-Audiences)')"
              :hint="t('admin.oauthScopes.resources.hint', {}, 'API audiences a token carrying this scope is valid for.')">
              <EditableStringList
                v-model="form.Resources"
                :placeholder="t('admin.oauthScopes.resource.placeholder', {}, 'event-tree-api')" />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.oauthScopes.userClaims', {}, 'User Claims')"
              :hint="t('admin.oauthScopes.userClaims.hint', {}, 'OIDC claim names added to the token / UserInfo when this scope is granted.')">
              <EditableStringList
                v-model="form.UserClaims"
                :placeholder="t('admin.oauthScopes.userClaim.placeholder', {}, 'email')" />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Options -->
        <section class="form-section">
          <h3 class="form-section-heading">{{ t('admin.oauthScopes.section.options', {}, 'Options') }}</h3>
          <div class="modal-form-grid">
            <CoarFormField class="col-half">
              <CoarCheckbox v-model="form.Enabled" :label="t('common.enabled', {}, 'Enabled')" />
              <p class="field-hint">{{ t('admin.oauthScopes.enabled.hint', {}, '(Default: on) The scope can be requested.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-half">
              <CoarCheckbox v-model="form.Required" :label="t('admin.oauthScopes.required', {}, 'Required')" />
              <p class="field-hint">{{ t('admin.oauthScopes.required.hint', {}, 'Cannot be deselected on the consent screen.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-half">
              <CoarCheckbox v-model="form.Emphasize" :label="t('admin.oauthScopes.emphasize', {}, 'Hervorheben')" />
              <p class="field-hint">{{ t('admin.oauthScopes.emphasize.hint', {}, 'Highlight as security-relevant on the consent screen.') }}</p>
            </CoarFormField>
            <CoarFormField class="col-half">
              <CoarCheckbox v-model="form.ShowInDiscoveryDocument" :label="t('admin.oauthScopes.showInDiscovery', {}, 'Show in Discovery')" />
              <p class="field-hint">{{ t('admin.oauthScopes.showInDiscovery.hint', {}, 'Visible in the public OIDC discovery document. (Default: on)') }}</p>
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.oauthScopes.allowDcr', {}, 'Dynamic Client Registration')"
              :hint="t('admin.oauthScopes.allowDcr.hint', {}, 'Clients registered via DCR may only request this scope when this is enabled.')">
              <CoarCheckbox
                v-model="form.AllowDynamicRegistrationClients"
                :label="t('admin.oauthScopes.allowDcr.toggle', {}, 'DCR clients may request this scope')" />
            </CoarFormField>
          </div>
        </section>

        <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
      </div>
    </div>
  </ModalLayout>
</template>

<style scoped>
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
