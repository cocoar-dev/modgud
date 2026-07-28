<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { CoarButton, CoarCard, CoarTag, CoarPopconfirm } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import Notice from '@/components/Notice.vue'
import { useUI } from '@/composables/useUI'
import { useAssets } from '@/composables/useAssets'
import type { AssetDto } from '@/models/assets'

const { t, language } = useI18n()
const ui = useUI()
const { assets, loading, error, list, upload, remove } = useAssets()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  ctx.header.subTitle = t('admin.assets.title', {}, 'Asset Library')
  ctx.header.icon = 'image'
  ctx.content.container = false
}), { immediate: true })

const fileInput = ref<HTMLInputElement | null>(null)
const uploading = ref(false)
const uploadError = ref<string | null>(null)
const referencesBlock = ref<{ assetId: string, references: string[] } | null>(null)
const dragOver = ref(false)

async function handleFiles(files: FileList | File[] | null) {
  if (!files || (files instanceof FileList ? files.length : files.length) === 0) return
  uploadError.value = null
  uploading.value = true
  try {
    const fileList = files instanceof FileList ? Array.from(files) : files
    for (const file of fileList) {
      const result = await upload(file)
      if ('error' in result) {
        uploadError.value = `${file.name}: ${result.error}`
        break
      }
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
  handleFiles(input.files)
  input.value = ''
}

function onDrop(event: DragEvent) {
  event.preventDefault()
  dragOver.value = false
  handleFiles(event.dataTransfer?.files ?? null)
}

async function deleteAsset(asset: AssetDto) {
  const r = await remove(asset.Id)
  if (r === true) return
  if ('inUse' in r) {
    referencesBlock.value = { assetId: asset.Id, references: r.inUse.References }
    return
  }
  uploadError.value = r.error
}

function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(2)} MB`
}

onMounted(() => list())
</script>

<template>
  <div class="asset-page">
    <Notice v-if="error" variant="error">{{ error }}</Notice>
    <Notice v-if="uploadError" variant="error" @click="uploadError = null">
      {{ uploadError }}
    </Notice>
    <Notice v-if="referencesBlock" variant="warning">
      {{ t('admin.assets.inUseHeadline', {}, 'Cannot delete — still referenced by:') }}
      <ul class="ml-4 mt-1 list-disc">
        <li v-for="ref in referencesBlock.references" :key="ref">{{ ref }}</li>
      </ul>
      <CoarButton size="s" variant="ghost" class="mt-2" @click="referencesBlock = null">
        {{ t('common.ok', {}, 'OK') }}
      </CoarButton>
    </Notice>

    <div class="upload-area"
      :class="{ 'upload-area-drag': dragOver }"
      @dragenter.prevent="dragOver = true"
      @dragover.prevent="dragOver = true"
      @dragleave.prevent="dragOver = false"
      @drop="onDrop">
      <p class="upload-area-text">
        {{ t('admin.assets.dropHere', {}, 'Drop image files here or click below to select. PNG, JPEG, GIF, WebP, SVG, ICO — max 2 MB each.') }}
      </p>
      <CoarButton :loading="uploading" @click="pickFiles">
        {{ t('admin.assets.selectFiles', {}, 'Select files…') }}
      </CoarButton>
      <input ref="fileInput" type="file" multiple
        accept="image/png,image/jpeg,image/gif,image/webp,image/svg+xml,image/x-icon,image/vnd.microsoft.icon"
        class="hidden"
        @change="onFileInputChange" />
    </div>

    <div v-if="loading" class="text-sm text-gray-400">{{ t('common.loading', {}, 'Loading…') }}</div>
    <div v-else-if="assets.length === 0 && !uploading" class="text-sm text-gray-400">
      {{ t('admin.assets.empty', {}, 'No assets uploaded yet.') }}
    </div>
    <div v-else class="asset-grid">
      <CoarCard v-for="asset in assets" :key="asset.Id" class="asset-card">
        <div class="asset-preview">
          <img :src="asset.Url" :alt="asset.FileName" />
        </div>
        <div class="asset-meta">
          <div class="asset-name" :title="asset.FileName">{{ asset.FileName }}</div>
          <div class="asset-detail">
            <CoarTag variant="neutral">{{ asset.ContentType.replace('image/', '') }}</CoarTag>
            <span>{{ formatBytes(asset.SizeBytes) }}</span>
          </div>
        </div>
        <div class="asset-delete">
          <CoarPopconfirm
            :title="t('admin.assets.deleteConfirm', {}, 'Delete this asset?')"
            :message="t('admin.assets.deleteWarning', {}, 'The asset will be permanently removed. Items currently referencing it will block the delete.')"
            @confirmed="deleteAsset(asset)">
            <CoarButton size="s" variant="ghost" icon-start="trash-2" :title="t('common.delete', {}, 'Delete')" />
          </CoarPopconfirm>
        </div>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.asset-page {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1rem;
  min-height: 0;
  flex: 1;
}

.upload-area {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.75rem;
  padding: 1.5rem;
  border: 2px dashed var(--coar-border-neutral-secondary);
  border-radius: 0.5rem;
  background: var(--coar-background-neutral-secondary);
  transition: border-color 100ms ease, background 100ms ease;
}

.upload-area-drag {
  border-color: var(--coar-text-accent-primary, #4f46e5);
  background: var(--coar-background-accent-subtle, rgba(79, 70, 229, 0.05));
}

.upload-area-text {
  margin: 0;
  font-size: 0.85rem;
  color: var(--coar-text-neutral-secondary);
  text-align: center;
}

.hidden { display: none; }

.asset-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 0.75rem;
  overflow-y: auto;
}

.asset-card {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.75rem;
  align-items: stretch;
  position: relative;
}

.asset-delete {
  position: absolute;
  top: 0.25rem;
  right: 0.25rem;
  background: var(--coar-background-neutral-primary, white);
  border-radius: 50%;
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.15);
}

.asset-preview {
  height: 100px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--coar-background-neutral-primary);
  border-radius: 0.25rem;
  overflow: hidden;
}

.asset-preview img {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
}

.asset-meta {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.asset-name {
  font-size: 0.85rem;
  font-weight: 600;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.asset-detail {
  display: flex;
  gap: 0.5rem;
  align-items: center;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary);
}
</style>
