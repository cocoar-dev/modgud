<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarNote, CoarTag, CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useApplicationsStore } from '@/stores/applications.store'
import type { ApplicationDto } from '@/models/application'

const { t } = useI18n()

// `id` from the routed modal is the App's Id (or "create" for a new one).
const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const id = computed(() => props.id)
const store = useApplicationsStore()
const isCreate = computed(() => id.value === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

// Klick-Aktion state — feedback after the default resource-server is
// provisioned, including the one-time secret to copy.
const rsBusy = ref(false)
const rsResult = ref<{ apiId: string; name: string; secret: string | null; alreadyExisted: boolean } | null>(null)
const rsError = ref<string | null>(null)

interface FormState {
  Slug: string
  DisplayName: string
  Description: string
  Resources: string  // newline-separated in the textarea
}

function emptyForm(): FormState {
  return { Slug: '', DisplayName: '', Description: '', Resources: '' }
}

const form = ref<FormState>(emptyForm())
const dto = ref<ApplicationDto | null>(null)

function fromDto(d: ApplicationDto): FormState {
  return {
    Slug: d.Slug,
    DisplayName: d.DisplayName,
    Description: d.Description ?? '',
    Resources: (d.Resources ?? []).join('\n'),
  }
}

function splitLines(input: string): string[] {
  return input.split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)
}

const isSystem = computed(() => dto.value?.IsSystem === true)

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.apps.createTitle', {}, 'Application erstellen')
    : (form.value.DisplayName || form.value.Slug),
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Slug)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Erstellen') : t('common.save', {}, 'Speichern'),
  disabled: loading.value
    || !form.value.DisplayName.trim()
    || (isCreate.value && !form.value.Slug.trim()),
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  if (isCreate.value) return
  loading.value = true
  try {
    const loaded = await store.loadOne(id.value)
    if (!loaded) {
      error.value = t('admin.apps.loadFailed', {}, 'Application konnte nicht geladen werden.')
      return
    }
    dto.value = loaded
    form.value = fromDto(loaded)
  } finally {
    loading.value = false
  }
})

async function save() {
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      await store.create({
        Slug: form.value.Slug.trim(),
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Resources: splitLines(form.value.Resources),
      })
    } else {
      await store.update(id.value, {
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Resources: splitLines(form.value.Resources),
      })
    }
    props.close()
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

async function provisionDefaultResourceServer() {
  rsBusy.value = true
  rsError.value = null
  try {
    const result = await store.createDefaultResourceServer(id.value)
    rsResult.value = {
      apiId: result.ApiId,
      name: result.Name,
      secret: result.ApiSecret,
      alreadyExisted: result.AlreadyExisted,
    }
  } catch (e: any) {
    rsError.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    rsBusy.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="layout-grid"
    :footer-button="footerButton" width="42rem">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote v-if="isCreate" variant="info">
        {{ t('admin.apps.createHint', {}, 'Eine neue App registriert sich für Permission-Resolution. Slug ist nach dem Erstellen unveränderbar — er prefixiert alle Permission-Strings dieser App.') }}
      </CoarNote>
      <CoarNote v-else-if="isSystem" variant="warning">
        {{ t('admin.apps.systemHint', {}, 'Dies ist die System-App (cocoar-auth). Sie kann nicht gelöscht oder umbenannt werden; Resources nur mit Bedacht ändern — die hier registrierten Resources müssen mit ResourceRegistry im Backend konsistent bleiben.') }}
      </CoarNote>

      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.apps.slug', {}, 'Slug (immutable)')">
          <CoarTextInput v-model="form.Slug" :disabled="!isCreate" clearable
            :placeholder="t('admin.apps.slugPlaceholder', {}, 'kebab-case-slug')" />
        </CoarFormField>
        <CoarFormField :label="t('admin.apps.displayName', {}, 'Display Name')">
          <CoarTextInput v-model="form.DisplayName" clearable />
        </CoarFormField>
      </div>

      <CoarFormField :label="t('common.description', {}, 'Beschreibung')">
        <CoarTextInput v-model="form.Description" clearable />
      </CoarFormField>

      <CoarFormField :label="t('admin.apps.resources', {}, 'Resources (eine pro Zeile)')">
        <textarea v-model="form.Resources" rows="6" class="textarea"
          placeholder="todo&#10;project&#10;tag" />
      </CoarFormField>

      <div v-if="!isCreate && dto" class="flex items-center gap-2 text-sm text-gray-500">
        <CoarTag v-if="isSystem" size="s" variant="warning">{{ t('admin.apps.systemTag', {}, 'System') }}</CoarTag>
      </div>

      <!-- Klick-Aktion: provision default resource-server. Lives at the
           bottom of the form because it's a one-time setup step admins
           reach for after creating the App. -->
      <div v-if="!isCreate && dto && !isSystem" class="rs-panel">
        <div class="rs-panel-header">
          {{ t('admin.apps.rs.title', {}, 'Resource Server') }}
        </div>

        <div v-if="!rsResult" class="text-xs text-gray-500">
          {{ t('admin.apps.rs.help', {}, 'A resource server identity lets your backend authenticate against /api/v1/distribution/* on behalf of users. The default one matches this app\'s slug; you can add more later in the OAuth APIs admin.') }}
        </div>

        <CoarNote v-if="rsResult?.alreadyExisted" variant="info">
          {{ t('admin.apps.rs.alreadyExists', { name: rsResult.name }, `Default resource server "${rsResult.name}" already exists. Manage its secrets in the OAuth APIs admin.`) }}
        </CoarNote>

        <CoarNote v-else-if="rsResult?.secret" variant="warning">
          <div class="font-semibold mb-1">
            {{ t('admin.apps.rs.created', { name: rsResult.name }, `Default resource server "${rsResult.name}" created.`) }}
          </div>
          <div class="text-xs mb-1">
            {{ t('admin.apps.rs.secretWarning', {}, 'Copy this API secret now — it will never be shown again.') }}
          </div>
          <code class="rs-secret">{{ rsResult.secret }}</code>
        </CoarNote>

        <p v-if="rsError" class="text-sm text-red-600">{{ rsError }}</p>

        <div v-if="!rsResult || rsResult.alreadyExisted" class="mt-2">
          <CoarButton
            size="s"
            icon-start="server"
            :loading="rsBusy"
            :disabled="rsBusy || (rsResult?.alreadyExisted ?? false)"
            @click="provisionDefaultResourceServer">
            {{ t('admin.apps.rs.create', {}, 'Create default resource server') }}
          </CoarButton>
        </div>
      </div>

      <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.rs-panel {
  border-top: 1px solid var(--coar-border-neutral-secondary, #e5e7eb);
  padding-top: 0.75rem;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.rs-panel-header {
  font-size: 0.8rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
}

.rs-secret {
  display: block;
  padding: 6px 8px;
  background: var(--coar-background-neutral-tertiary, #f3f4f6);
  border-radius: var(--coar-radius-s, 3px);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.78rem;
  word-break: break-all;
}

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
