<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { CoarPageBuilder, type PageNode } from '@cocoar/vue-page-builder'
import { CoarButton, CoarNote, CoarTextInput, CoarFormField, useDialog } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import AssetPicker from '@/components/AssetPicker.vue'
import type { AssetDto } from '@/models/assets'
import {
  AUTH_PAGE_SLOTS,
  createAuthPageConfig,
  createDefaultAuthPageSchema,
  type AuthPageSlot,
} from '@/page-builder/authPageConfig'
import { useRealmPagesApi, type VariantPayload } from '@/composables/usePagesApi'

const { t, language } = useI18n()
const ui = useUI()
const route = useRoute()
const router = useRouter()
const dialog = useDialog()
const api = useRealmPagesApi()

const slug = computed(() => (route.params.slug as string) ?? '')
const slot = computed<AuthPageSlot>(() =>
  AUTH_PAGE_SLOTS.includes(slug.value as AuthPageSlot) ? slug.value as AuthPageSlot : 'login')
const variantId = computed(() => (route.params.variantId as string) ?? 'new')
const isNew = computed(() => variantId.value === 'new')

const pageConfig = computed(() => createAuthPageConfig(slot.value, async (currentId?: string) => {
  const ref$ = dialog.open<AssetDto>(AssetPicker, {
    title: t('asset.picker.title', {}, 'Select an asset'),
    size: 'l',
  }, { selectedId: currentId ?? null })
  const result = await ref$.result
  return result?.Id ?? null
}))

const labelBySlot: Record<string, string> = {
  login: t('admin.customization.pages.login.title', {}, 'Login'),
  logout: t('admin.customization.pages.logout.title', {}, 'Logout'),
  'password-forgot': t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password'),
}

watch([language, slug], () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  const scope = t('admin.customization.pages.title', {}, 'Pages')
  ctx.header.subTitle = `${scope} · ${labelBySlot[slug.value] ?? slug.value}`
  ctx.header.icon = 'layout-template'
  ctx.content.container = false
}), { immediate: true })

const name = ref('')
const schema = ref<PageNode>(createDefaultAuthPageSchema(slot.value))
const loading = ref(true)
const saving = ref(false)
const savedFlash = ref(false)
const error = ref<string | null>(null)
const resetHint = ref(false)

async function load() {
  loading.value = true
  error.value = null
  resetHint.value = false
  try {
    if (isNew.value) {
      name.value = ''
      schema.value = createDefaultAuthPageSchema(slot.value)
      return
    }
    const variant = await api.getVariant(slot.value, variantId.value)
    name.value = variant.Name
    try {
      schema.value = JSON.parse(variant.Schema) as PageNode
    } catch (e: any) {
      error.value = `Stored schema is invalid JSON: ${e?.message ?? e}`
      schema.value = createDefaultAuthPageSchema(slot.value)
    }
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!name.value.trim()) {
    error.value = t('admin.customization.pages.nameRequired', {}, 'Give this variant a name first.')
    return
  }
  saving.value = true
  error.value = null
  resetHint.value = false
  try {
    const payload: VariantPayload = { Name: name.value.trim(), Schema: JSON.stringify(schema.value) }
    if (isNew.value) {
      const created = await api.createVariant(slot.value, payload)
      flashSaved()
      // Swap the URL to the freshly-created variant so subsequent saves update it.
      await router.replace(`/platform/customization/pages/${slot.value}/${created.Id}`)
    } else {
      await api.updateVariant(slot.value, variantId.value, payload)
      flashSaved()
    }
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

// UI-only: load the built-in default template into the editor buffer. Nothing is
// persisted until Save (ADR-0001 — reset is non-destructive).
function loadDefaultTemplate() {
  schema.value = createDefaultAuthPageSchema(slot.value)
  resetHint.value = true
}

function flashSaved() {
  savedFlash.value = true
  setTimeout(() => { savedFlash.value = false }, 1500)
}

function back() {
  router.push('/platform/customization/pages')
}

watch([slot, variantId], load, { immediate: true })
</script>

<template>
  <div class="editor-page">
    <div class="editor-toolbar">
      <CoarButton size="s" variant="ghost" icon-start="arrow-left" @click="back">
        {{ t('common.back', {}, 'Back') }}
      </CoarButton>
      <CoarFormField class="name-field">
        <CoarTextInput
          v-model="name"
          size="s"
          :placeholder="t('admin.customization.pages.variantName', {}, 'Variant name')" />
      </CoarFormField>
      <div class="toolbar-spacer" />
      <CoarButton size="s" variant="ghost" @click="loadDefaultTemplate">
        {{ t('admin.customization.pages.loadDefault', {}, 'Load built-in template') }}
      </CoarButton>
      <CoarButton size="s" :loading="saving" @click="save">
        {{ isNew ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save') }}
      </CoarButton>
    </div>

    <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
    <CoarNote v-if="resetHint" variant="info">
      {{ t('admin.customization.pages.resetHint', {}, 'Loaded the built-in template into the editor. Nothing is saved until you click Save.') }}
    </CoarNote>
    <CoarNote v-if="savedFlash" variant="success">
      {{ t('admin.realmSettings.saved', {}, 'Saved.') }}
    </CoarNote>

    <div v-if="loading" class="text-sm text-gray-400">{{ t('common.loading', {}, 'Loading…') }}</div>
    <CoarPageBuilder v-else v-model="schema" :config="pageConfig" class="builder" />
  </div>
</template>

<style scoped>
.editor-page {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 0.5rem 1rem 1rem;
  min-height: 0;
  flex: 1;
}

.editor-toolbar {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.25rem 0;
}

.name-field { margin: 0; min-width: 220px; }
.toolbar-spacer { flex: 1; }

.builder {
  flex: 1;
  min-height: 0;
}
</style>
