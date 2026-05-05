<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { CoarTextInput, CoarFormField, CoarCheckbox, CoarNote } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useRealmStore } from '@/stores/realm.store'
import type { RealmDto } from '@/models/realm'

const { t } = useI18n()

// `id` from the routed modal carries the realm's Slug (the URL key for realms).
const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const slug = computed(() => props.id)
const store = useRealmStore()
const isCreate = computed(() => slug.value === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

interface FormState {
  Slug: string
  DisplayName: string
  Description: string
  Domains: string  // newline
  IsControlPlane: boolean
  IsActive: boolean
}

function emptyForm(): FormState {
  return {
    Slug: '',
    DisplayName: '',
    Description: '',
    Domains: '',
    IsControlPlane: false,
    IsActive: true,
  }
}

const form = ref<FormState>(emptyForm())
const dto = ref<RealmDto | null>(null)

function fromDto(dto: RealmDto): FormState {
  return {
    Slug: dto.Slug,
    DisplayName: dto.DisplayName,
    Description: dto.Description ?? '',
    Domains: (dto.Domains ?? []).join('\n'),
    IsControlPlane: dto.IsControlPlane,
    IsActive: dto.IsActive,
  }
}

function splitLines(input: string): string[] {
  return input.split(/[\r\n]+/).map((s) => s.trim()).filter(Boolean)
}

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.realms.createTitle', {}, 'Realm erstellen')
    : (form.value.DisplayName || form.value.Slug)
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
    const loaded = await store.loadOne(slug.value)
    if (!loaded) {
      error.value = t('admin.realms.loadFailed', {}, 'Realm konnte nicht geladen werden.')
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
        Domains: splitLines(form.value.Domains),
        IsControlPlane: form.value.IsControlPlane,
      })
    } else {
      await store.update(slug.value, {
        DisplayName: form.value.DisplayName.trim(),
        Description: form.value.Description.trim() || null,
        Domains: splitLines(form.value.Domains),
        IsControlPlane: form.value.IsControlPlane,
        IsActive: form.value.IsActive,
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
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="globe"
    :footer-button="footerButton" width="42rem">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote v-if="isCreate" variant="info">
        {{ t('admin.realms.createHint', {}, 'Beim Anlegen wird automatisch eine eigene Datenbank provisioniert und mit Default-OAuth-Scopes geseedet.') }}
      </CoarNote>

      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('admin.realms.slug', {}, 'Slug (immutable)')">
          <CoarTextInput v-model="form.Slug" :disabled="!isCreate" clearable
            :placeholder="t('admin.realms.slugPlaceholder', {}, 'kebab-case-slug')" />
        </CoarFormField>
        <CoarFormField :label="t('admin.realms.displayName', {}, 'Display Name')">
          <CoarTextInput v-model="form.DisplayName" clearable />
        </CoarFormField>
      </div>

      <CoarFormField :label="t('common.description', {}, 'Beschreibung')">
        <CoarTextInput v-model="form.Description" clearable />
      </CoarFormField>

      <CoarFormField :label="t('admin.realms.domains', {}, 'Domains (eine pro Zeile)')">
        <textarea v-model="form.Domains" rows="3" class="textarea"
          placeholder="example.com&#10;auth.example.com" />
      </CoarFormField>

      <div class="flex flex-wrap gap-x-6 gap-y-2 mt-1">
        <CoarCheckbox v-model="form.IsControlPlane"
          :label="t('admin.realms.isControlPlane', {}, 'Control Plane (cross-realm Admin-Oberfläche)')" />
        <CoarCheckbox v-if="!isCreate" v-model="form.IsActive"
          :label="t('common.active', {}, 'Aktiv')" />
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
