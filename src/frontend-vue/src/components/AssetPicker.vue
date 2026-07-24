<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { CoarButton, CoarCard } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import AppNote from '@/components/AppNote.vue'
import { useAssets } from '@/composables/useAssets'
import type { AssetDto } from '@/models/assets'

// Designed for useDialog().open() — the `close` callback is injected by
// the dialog host and accepts the picked AssetDto as the result. Calling
// close() with no argument is "cancelled".
const props = defineProps<{
  /** Currently-selected asset id, if any. The card gets a highlighted border. */
  selectedId?: string | null
  /** Injected by @cocoar/vue-ui useDialog. */
  close: (asset?: AssetDto) => void
}>()

const { t } = useI18n()
const { assets, loading, error, list, upload } = useAssets()

const fileInput = ref<HTMLInputElement | null>(null)
const uploading = ref(false)
const uploadError = ref<string | null>(null)

async function handleFile(file: File) {
  uploadError.value = null
  uploading.value = true
  try {
    const result = await upload(file)
    if ('error' in result) {
      uploadError.value = result.error
    } else {
      // Auto-select the just-uploaded asset for a smooth single-click flow.
      props.close(result)
    }
  } finally {
    uploading.value = false
  }
}

function pickFiles() {
  fileInput.value?.click()
}

function onFileInputChange(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file) handleFile(file)
  input.value = ''
}

onMounted(() => list())
</script>

<template>
  <div class="picker">
    <div class="picker-header">
      <h2 class="picker-title">{{ t('asset.picker.title', {}, 'Select an asset') }}</h2>
      <div class="picker-actions">
        <CoarButton size="s" :loading="uploading" @click="pickFiles">
          {{ t('asset.picker.upload', {}, 'Upload new…') }}
        </CoarButton>
        <CoarButton size="s" variant="ghost" @click="props.close()">
          {{ t('common.cancel', {}, 'Cancel') }}
        </CoarButton>
      </div>
      <input ref="fileInput" type="file"
        accept="image/png,image/jpeg,image/gif,image/webp,image/svg+xml,image/x-icon,image/vnd.microsoft.icon"
        class="hidden"
        @change="onFileInputChange" />
    </div>

    <AppNote v-if="error" variant="error" :truncate="false">{{ error }}</AppNote>
    <AppNote v-if="uploadError" variant="error" :truncate="false">{{ uploadError }}</AppNote>

    <div v-if="loading" class="picker-empty">{{ t('common.loading', {}, 'Loading…') }}</div>
    <div v-else-if="assets.length === 0" class="picker-empty">
      {{ t('asset.picker.empty', {}, 'No assets yet — upload one to get started.') }}
    </div>
    <div v-else class="picker-grid">
      <CoarCard
        v-for="asset in assets"
        :key="asset.Id"
        :class="['picker-tile', { 'picker-tile-selected': props.selectedId === asset.Id }]"
        @click="props.close(asset)">
        <div class="picker-preview">
          <img :src="asset.Url" :alt="asset.FileName" />
        </div>
        <div class="picker-name" :title="asset.FileName">{{ asset.FileName }}</div>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.picker {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  min-height: 0;
  max-height: 70vh;
  width: 60vw;
  max-width: 720px;
  padding: 1rem;
}

.picker-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 0.5rem;
}

.picker-title {
  margin: 0;
  font-size: 1rem;
  font-weight: 600;
}

.picker-actions {
  display: flex;
  gap: 0.5rem;
}

.hidden { display: none; }

.picker-empty {
  padding: 2rem;
  text-align: center;
  color: var(--coar-text-neutral-secondary);
  font-size: 0.875rem;
}

.picker-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 0.5rem;
  overflow-y: auto;
}

.picker-tile {
  display: flex;
  flex-direction: column;
  gap: 0.375rem;
  padding: 0.5rem;
  cursor: pointer;
  transition: transform 80ms ease, box-shadow 80ms ease;
  border: 2px solid transparent;
}

.picker-tile:hover {
  transform: translateY(-1px);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);
}

.picker-tile-selected {
  border-color: var(--coar-text-accent-primary, #4f46e5);
}

.picker-preview {
  height: 80px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--coar-background-neutral-primary);
  border-radius: 0.25rem;
  overflow: hidden;
}

.picker-preview img {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
}

.picker-name {
  font-size: 0.75rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  text-align: center;
}
</style>
