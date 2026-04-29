<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarSelect } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useApplicationsStore } from '@/stores/applications.store'
import type { OAuthScopeDto } from '@/models/oauth'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useOAuthScopeStore()
const applicationsStore = useApplicationsStore()
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
  Resources: string  // newline-separated
  UserClaims: string  // newline-separated
  Enabled: boolean
  Required: boolean
  Emphasize: boolean
  ShowInDiscoveryDocument: boolean
  /** Empty string = "global", otherwise an App.Id. */
  AppId: string
}

function emptyForm(): FormState {
  return {
    Name: '',
    DisplayName: '',
    Description: '',
    Resources: '',
    UserClaims: '',
    Enabled: true,
    Required: false,
    Emphasize: false,
    ShowInDiscoveryDocument: true,
    AppId: '',
  }
}

const form = ref<FormState>(emptyForm())

function fromDto(dto: OAuthScopeDto): FormState {
  return {
    Name: dto.Name,
    DisplayName: dto.DisplayName ?? '',
    Description: dto.Description ?? '',
    Resources: (dto.Resources ?? []).join('\n'),
    UserClaims: (dto.UserClaims ?? []).join('\n'),
    Enabled: dto.Enabled,
    Required: dto.Required,
    Emphasize: dto.Emphasize,
    ShowInDiscoveryDocument: dto.ShowInDiscoveryDocument,
    AppId: dto.AppId ?? '',
  }
}

function splitLines(input: string): string[] {
  return input.split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)
}

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.oauthScopes.createTitle', {}, 'Scope erstellen')
    : (form.value.DisplayName || form.value.Name)
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Name)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Erstellen') : t('common.save', {}, 'Speichern'),
  disabled: !form.value.Name.trim() || loading.value,
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  applicationsStore.initialize()
  if (isCreate.value) return
  loading.value = true
  try {
    const dto = await store.loadOne(props.id)
    if (!dto) {
      error.value = t('admin.oauthScopes.loadFailed', {}, 'Scope konnte nicht geladen werden.')
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
        Resources: splitLines(form.value.Resources),
        UserClaims: splitLines(form.value.UserClaims),
        Enabled: form.value.Enabled,
        Required: form.value.Required,
        Emphasize: form.value.Emphasize,
        ShowInDiscoveryDocument: form.value.ShowInDiscoveryDocument,
        AppId: form.value.AppId || null,
      })
    } else {
      await store.update(props.id, {
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Resources: splitLines(form.value.Resources),
        UserClaims: splitLines(form.value.UserClaims),
        Enabled: form.value.Enabled,
        Required: form.value.Required,
        Emphasize: form.value.Emphasize,
        ShowInDiscoveryDocument: form.value.ShowInDiscoveryDocument,
        // Always send — empty string = make global, guid = assign.
        AppId: form.value.AppId,
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
    :footer-button="footerButton" width="40rem">
    <div v-if="loading && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.oauthScopes.name', {}, 'Name')">
          <CoarTextInput v-model="form.Name" :disabled="!isCreate" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.oauthScopes.displayName', {}, 'Display Name')">
          <CoarTextInput v-model="form.DisplayName" clearable />
        </CoarFormField>
      </div>
      <CoarFormField :label="t('admin.oauthScopes.description', {}, 'Beschreibung')">
        <CoarTextInput v-model="form.Description" clearable />
      </CoarFormField>
      <CoarFormField :label="t('admin.oauthScopes.app', {}, 'Application')">
        <CoarSelect v-model="form.AppId" :options="appOptions" />
        <p class="text-xs text-gray-500 mt-1">
          {{ form.AppId
            ? t('admin.oauthScopes.app.scopedHint', {}, 'Only OAuth clients linked to this App may request this scope.')
            : t('admin.oauthScopes.app.globalHint', {}, 'Cross-app scope (e.g. standard OIDC scopes). Any client may request it.') }}
        </p>
      </CoarFormField>
      <CoarFormField :label="t('admin.oauthScopes.resources', {}, 'Resources (eine pro Zeile, z.B. API-Audiences)')">
        <textarea v-model="form.Resources" rows="3" class="textarea" />
      </CoarFormField>
      <CoarFormField :label="t('admin.oauthScopes.userClaims', {}, 'User Claims (eine pro Zeile)')">
        <textarea v-model="form.UserClaims" rows="3" class="textarea" />
      </CoarFormField>
      <div class="flex flex-wrap gap-x-6 gap-y-2 mt-1">
        <CoarCheckbox v-model="form.Enabled" :label="t('common.enabled', {}, 'Aktiviert')" />
        <CoarCheckbox v-model="form.Required" :label="t('admin.oauthScopes.required', {}, 'Pflicht')" />
        <CoarCheckbox v-model="form.Emphasize" :label="t('admin.oauthScopes.emphasize', {}, 'Hervorheben')" />
        <CoarCheckbox v-model="form.ShowInDiscoveryDocument" :label="t('admin.oauthScopes.showInDiscovery', {}, 'In Discovery anzeigen')" />
      </div>
      <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
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
