<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import {
  CoarCard,
  CoarTextInput,
  CoarFormField,
  CoarNote,
  CoarButton,
  useDialog,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useRealmSettingsStore } from '@/stores/realmSettings.store'
import AssetPicker from '@/components/AssetPicker.vue'
import type { AssetDto } from '@/models/assets'
import type {
  BrandingSettingsDto,
  UpdateBrandingSettingsDto,
} from '@/models/realmSettings'

const { t, language } = useI18n()
const ui = useUI()
const settingsStore = useRealmSettingsStore()
const dialog = useDialog()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Plattform')
  ctx.header.subTitle = t('admin.customization.branding.title', {}, 'Branding')
  ctx.header.icon = 'palette'
  ctx.content.container = false
}), { immediate: true })

interface BrandingFormState {
  ProductName: string
  LogoAssetId: string
  LogoUrl: string
  FaviconAssetId: string
  FaviconUrl: string
  PrimaryColor: string
}

function empty(): BrandingFormState {
  return {
    ProductName: '',
    LogoAssetId: '',
    LogoUrl: '',
    FaviconAssetId: '',
    FaviconUrl: '',
    PrimaryColor: '',
  }
}

function fromDto(b: BrandingSettingsDto): BrandingFormState {
  return {
    ProductName: b.ProductName ?? '',
    LogoAssetId: b.LogoAssetId ?? '',
    LogoUrl: b.LogoUrl ?? '',
    FaviconAssetId: b.FaviconAssetId ?? '',
    FaviconUrl: b.FaviconUrl ?? '',
    PrimaryColor: b.PrimaryColor ?? '',
  }
}

const form = ref<BrandingFormState>(empty())
const original = ref<BrandingSettingsDto | null>(null)
const initialLoad = ref(true)
const saving = ref(false)
const error = ref<string | null>(null)
const savedFlash = ref(false)

async function pickLogo() {
  const ref$ = dialog.open<AssetDto>(AssetPicker, {
    title: t('admin.customization.branding.pickLogo', {}, 'Select logo'),
    size: 'l',
  }, { selectedId: form.value.LogoAssetId || null })
  const result = await ref$.result
  if (result) {
    form.value.LogoAssetId = result.Id
    form.value.LogoUrl = result.Url
  }
}

function clearLogo() {
  form.value.LogoAssetId = ''
  form.value.LogoUrl = ''
}

async function pickFavicon() {
  const ref$ = dialog.open<AssetDto>(AssetPicker, {
    title: t('admin.customization.branding.pickFavicon', {}, 'Select favicon'),
    size: 'l',
  }, { selectedId: form.value.FaviconAssetId || null })
  const result = await ref$.result
  if (result) {
    form.value.FaviconAssetId = result.Id
    form.value.FaviconUrl = result.Url
  }
}

function clearFavicon() {
  form.value.FaviconAssetId = ''
  form.value.FaviconUrl = ''
}

onMounted(async () => {
  initialLoad.value = true
  try {
    const dto = await settingsStore.load()
    original.value = dto.Branding
    form.value = fromDto(dto.Branding)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.message ?? String(e)
  } finally {
    initialLoad.value = false
  }
})

function buildPatch(): UpdateBrandingSettingsDto | undefined {
  const orig = original.value
  if (!orig) return undefined
  const cur = form.value
  const patch: UpdateBrandingSettingsDto = {}

  // Tri-state per field: trimmed-empty maps to "" (clear → revert to
  // default), changed value writes through, unchanged is omitted.
  const productName = cur.ProductName.trim()
  if (productName !== (orig.ProductName ?? '')) patch.ProductName = productName
  if (cur.LogoAssetId !== (orig.LogoAssetId ?? '')) patch.LogoAssetId = cur.LogoAssetId
  if (cur.FaviconAssetId !== (orig.FaviconAssetId ?? '')) patch.FaviconAssetId = cur.FaviconAssetId
  const color = cur.PrimaryColor.trim()
  if (color !== (orig.PrimaryColor ?? '')) patch.PrimaryColor = color

  return Object.keys(patch).length === 0 ? undefined : patch
}

async function save() {
  const patch = buildPatch()
  if (!patch) {
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1200)
    return
  }
  saving.value = true
  error.value = null
  try {
    const updated = await settingsStore.patch({ Branding: patch })
    original.value = updated.Branding
    form.value = fromDto(updated.Branding)
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1500)
  } catch (e: any) {
    error.value = e?.body?.detail ?? e?.body?.error ?? e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4 gap-3">
    <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
    <CoarNote v-if="savedFlash" variant="success">
      {{ t('admin.realmSettings.saved', {}, 'Saved.') }}
    </CoarNote>

    <div v-if="initialLoad" class="text-sm text-gray-400">
      {{ t('common.loading', {}, 'Loading...') }}
    </div>

    <CoarCard v-else class="p-4">
      <div class="flex flex-col gap-3">
        <p class="text-xs text-gray-500">
          {{ t('admin.customization.branding.hint', {}, 'Per-realm branding for the SPA shell. All fields optional — leave empty for the Cocoar defaults. Logo + Favicon are picked from the Asset Library. Changes apply on the next SPA load.') }}
        </p>

        <div class="grid grid-cols-2 gap-3">
          <CoarFormField :label="t('admin.customization.branding.productName', {}, 'Product name')">
            <CoarTextInput v-model="form.ProductName" clearable placeholder="Cocoar.Auth" />
          </CoarFormField>
          <CoarFormField :label="t('admin.customization.branding.primaryColor', {}, 'Primary color (CSS color, e.g. #5A6478)')">
            <CoarTextInput v-model="form.PrimaryColor" clearable placeholder="#5A6478" />
          </CoarFormField>
          <CoarFormField :label="t('admin.customization.branding.logo', {}, 'Logo')">
            <div class="asset-row">
              <div class="asset-thumb">
                <img v-if="form.LogoUrl" :src="form.LogoUrl" alt="logo" />
                <span v-else class="asset-thumb-empty">—</span>
              </div>
              <CoarButton size="s" variant="ghost" @click="pickLogo">
                {{ t('admin.customization.branding.pick', {}, 'Browse…') }}
              </CoarButton>
              <CoarButton v-if="form.LogoAssetId" size="s" variant="ghost" @click="clearLogo">
                {{ t('common.clear', {}, 'Clear') }}
              </CoarButton>
            </div>
          </CoarFormField>
          <CoarFormField :label="t('admin.customization.branding.favicon', {}, 'Favicon')">
            <div class="asset-row">
              <div class="asset-thumb">
                <img v-if="form.FaviconUrl" :src="form.FaviconUrl" alt="favicon" />
                <span v-else class="asset-thumb-empty">—</span>
              </div>
              <CoarButton size="s" variant="ghost" @click="pickFavicon">
                {{ t('admin.customization.branding.pick', {}, 'Browse…') }}
              </CoarButton>
              <CoarButton v-if="form.FaviconAssetId" size="s" variant="ghost" @click="clearFavicon">
                {{ t('common.clear', {}, 'Clear') }}
              </CoarButton>
            </div>
          </CoarFormField>
        </div>

        <div class="flex">
          <CoarButton :loading="saving" @click="save">
            {{ t('common.save', {}, 'Save') }}
          </CoarButton>
        </div>
      </div>
    </CoarCard>
  </div>
</template>

<style scoped>
.asset-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.asset-thumb {
  width: 48px;
  height: 48px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--coar-background-neutral-primary);
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: 0.25rem;
  overflow: hidden;
  flex-shrink: 0;
}

.asset-thumb img {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
}

.asset-thumb-empty {
  color: var(--coar-text-neutral-secondary);
  font-size: 0.75rem;
}
</style>
