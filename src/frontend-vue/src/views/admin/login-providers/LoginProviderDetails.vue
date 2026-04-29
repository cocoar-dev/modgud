<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  CoarTextInput,
  CoarFormField,
  CoarSelect,
  CoarButton,
  CoarIcon,
  CoarNote,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import type { LoginProviderDto, LoginProviderType } from '@/models/loginProvider'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useLoginProviderStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

const typeOptions: { value: LoginProviderType; label: string }[] = [
  { value: 'Internal', label: 'Internal (built-in)' },
  { value: 'OpenIdConnect', label: 'OpenID Connect' },
]

interface ConfigEntry { key: string; value: string }

interface FormState {
  Name: string
  DisplayName: string
  Description: string
  Type: LoginProviderType
  Configuration: ConfigEntry[]
}

function emptyForm(): FormState {
  return {
    Name: '',
    DisplayName: '',
    Description: '',
    Type: 'OpenIdConnect',
    Configuration: [],
  }
}

const form = ref<FormState>(emptyForm())
const dto = ref<LoginProviderDto | null>(null)

function fromDto(dto: LoginProviderDto): FormState {
  return {
    Name: dto.Name,
    DisplayName: dto.DisplayName ?? '',
    Description: dto.Description ?? '',
    Type: dto.Type,
    Configuration: Object.entries(dto.Configuration ?? {}).map(([key, value]) => ({ key, value })),
  }
}

function configRecord(): Record<string, string> {
  const out: Record<string, string> = {}
  for (const e of form.value.Configuration) {
    const k = e.key.trim()
    if (k) out[k] = e.value
  }
  return out
}

const modalTitle = computed(() =>
  isCreate.value
    ? t('admin.loginProviders.createTitle', {}, 'Login-Provider erstellen')
    : (form.value.DisplayName || form.value.Name)
)
const modalSubtitle = computed(() => isCreate.value ? undefined : form.value.Name)

const isBuiltIn = computed(() => dto.value?.IsBuiltIn === true)

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Erstellen') : t('common.save', {}, 'Speichern'),
  disabled: !form.value.Name.trim() || loading.value,
  loading: loading.value,
  onClick: save,
}))

onMounted(async () => {
  if (isCreate.value) return
  loading.value = true
  try {
    const loaded = await store.loadOne(props.id)
    if (!loaded) {
      error.value = t('admin.loginProviders.loadFailed', {}, 'Provider konnte nicht geladen werden.')
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
      await store.create({
        Name: form.value.Name.trim(),
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Type: form.value.Type,
        Configuration: configRecord(),
      })
    } else {
      // Backend's UpdateLoginProviderDto only accepts Name/DisplayName/Description/Configuration
      // — Type is immutable after creation.
      await store.update(props.id, {
        Name: form.value.Name.trim(),
        DisplayName: form.value.DisplayName.trim() || null,
        Description: form.value.Description.trim() || null,
        Configuration: configRecord(),
      })
    }
    props.close()
  } catch (e: any) {
    error.value = e?.body?.Message ?? e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

function addConfigEntry() {
  form.value.Configuration.push({ key: '', value: '' })
}
function removeConfigEntry(idx: number) {
  form.value.Configuration.splice(idx, 1)
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" :sub-title="modalSubtitle" icon="log-in"
    :footer-button="footerButton" width="44rem">
    <div v-if="loading && !dto && !isCreate" class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Laden...') }}</span>
    </div>
    <div v-else class="flex flex-col min-w-0 min-h-0 flex-1 gap-3">
      <CoarNote v-if="isBuiltIn" variant="info">
        {{ t('admin.loginProviders.builtInHint', {}, 'Built-in Provider — kann nicht gelöscht werden, Konfiguration ist eingeschränkt.') }}
      </CoarNote>

      <div class="grid grid-cols-2 gap-3">
        <CoarFormField :label="t('common.name', {}, 'Name')">
          <CoarTextInput v-model="form.Name" :disabled="isBuiltIn" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.loginProviders.displayName', {}, 'Display Name')">
          <CoarTextInput v-model="form.DisplayName" clearable />
        </CoarFormField>
        <CoarFormField :label="t('admin.loginProviders.type', {}, 'Type')">
          <CoarSelect v-model="form.Type" :options="typeOptions" :disabled="!isCreate" />
        </CoarFormField>
      </div>

      <CoarFormField :label="t('common.description', {}, 'Beschreibung')">
        <CoarTextInput v-model="form.Description" clearable />
      </CoarFormField>

      <div class="config-section">
        <div class="flex items-center justify-between">
          <div class="section-heading">{{ t('admin.loginProviders.configuration', {}, 'Konfiguration') }}</div>
          <CoarButton size="s" variant="secondary" icon-start="plus" @click="addConfigEntry">
            {{ t('admin.loginProviders.addEntry', {}, 'Eintrag hinzufügen') }}
          </CoarButton>
        </div>
        <p class="text-xs text-gray-500">
          {{ t('admin.loginProviders.configHint', {}, 'Schlüssel/Wert-Paare. Provider-spezifisch (z.B. Authority, ClientId, ClientSecret für OIDC).') }}
        </p>
        <div v-if="form.Configuration.length === 0" class="text-xs text-gray-400 italic">
          {{ t('admin.loginProviders.noConfig', {}, 'Keine Konfiguration vorhanden.') }}
        </div>
        <div v-for="(entry, idx) in form.Configuration" :key="idx" class="config-row">
          <CoarTextInput v-model="entry.key" :placeholder="t('admin.loginProviders.configKey', {}, 'Schlüssel')" class="config-key" />
          <CoarTextInput v-model="entry.value" :placeholder="t('admin.loginProviders.configValue', {}, 'Wert')" class="config-value" />
          <button class="text-surface-400 hover:text-red-600 transition px-2"
            :title="t('common.delete', {}, 'Löschen')"
            @click="removeConfigEntry(idx)">
            <CoarIcon name="trash-2" size="s" />
          </button>
        </div>
      </div>

      <p v-if="error" class="text-sm text-red-600">{{ error }}</p>
    </div>
  </ModalLayout>
</template>

<style scoped>
.config-section {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-top: 8px;
}
.section-heading {
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: #525e76;
}
.config-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.config-key {
  flex: 1;
}
.config-value {
  flex: 2;
}
</style>
