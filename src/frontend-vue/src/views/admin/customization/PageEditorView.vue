<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { CoarPageBuilder, normalizePageSchema, type PageNode } from '@cocoar/vue-page-builder'
import { CoarNotice, CoarButton, CoarTextInput, CoarFormField, useDialog } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import AssetPicker from '@/components/AssetPicker.vue'
import type { AssetDto } from '@/models/assets'
import {
  AUTH_PAGE_SLOTS,
  authPageLocale,
  createAuthPageConfig,
  createDefaultAuthPageSchema,
  type AuthPageSlot,
} from '@/page-builder/authPageConfig'
import {
  useRealmPagesApi,
  type PageVariantRevision,
  type VariantPayload,
} from '@/composables/usePagesApi'

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

const pageConfig = computed(() => createAuthPageConfig(slot.value, authPageLocale(language.value), async (currentId?: string) => {
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
  consent: t('admin.customization.pages.consent.title', {}, 'Consent'),
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
const publishing = ref(false)
const publishedRevision = ref(0)
const publishedAt = ref<string | null>(null)
const hasUnpublishedChanges = ref(false)
const revisions = ref<PageVariantRevision[]>([])
const rollbackRevision = ref<number | null>(null)

async function load() {
  loading.value = true
  error.value = null
  resetHint.value = false
  try {
    if (isNew.value) {
      name.value = ''
      schema.value = createDefaultAuthPageSchema(slot.value)
      publishedRevision.value = 0
      publishedAt.value = null
      hasUnpublishedChanges.value = true
      revisions.value = []
      return
    }
    const variant = await api.getVariant(slot.value, variantId.value)
    name.value = variant.Name
    try {
      schema.value = normalizePageSchema(
        JSON.parse(variant.Schema),
        { elements: pageConfig.value.elements },
      ).schema
    } catch (e: any) {
      error.value = `Stored schema is invalid JSON: ${e?.message ?? e}`
      schema.value = createDefaultAuthPageSchema(slot.value)
    }
    publishedRevision.value = variant.PublishedRevision
    publishedAt.value = variant.PublishedAt
    hasUnpublishedChanges.value = variant.HasUnpublishedChanges
    revisions.value = variant.Revisions
    rollbackRevision.value = variant.Revisions[0]?.Number ?? null
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
      hasUnpublishedChanges.value = true
      flashSaved()
    }
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

async function publish() {
  if (isNew.value || publishing.value) return
  publishing.value = true
  error.value = null
  try {
    await save()
    if (error.value) return
    await api.publishVariant(slot.value, variantId.value)
    await load()
    flashSaved()
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    publishing.value = false
  }
}

async function rollback() {
  if (isNew.value || rollbackRevision.value === null || publishing.value) return
  publishing.value = true
  error.value = null
  try {
    await api.rollbackVariant(slot.value, variantId.value, rollbackRevision.value)
    await load()
    flashSaved()
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    publishing.value = false
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
      <span v-if="!isNew" class="revision-status" :title="publishedAt ?? undefined">
        {{ publishedRevision > 0 ? `Published r${publishedRevision}` : 'Not published' }}
        <template v-if="hasUnpublishedChanges"> · Draft changed</template>
      </span>
      <select v-if="revisions.length" v-model.number="rollbackRevision" class="revision-select">
        <option v-for="revision in revisions" :key="revision.Number" :value="revision.Number">
          r{{ revision.Number }} · {{ new Date(revision.PublishedAt).toLocaleString() }}
        </option>
      </select>
      <CoarButton v-if="revisions.length" size="s" variant="ghost" :disabled="publishing" @click="rollback">
        {{ t('admin.customization.pages.rollback', {}, 'Rollback') }}
      </CoarButton>
      <CoarButton size="s" variant="ghost" @click="loadDefaultTemplate">
        {{ t('admin.customization.pages.loadDefault', {}, 'Load built-in template') }}
      </CoarButton>
      <CoarButton size="s" :loading="saving" @click="save">
        {{ isNew ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save') }}
      </CoarButton>
      <CoarButton v-if="!isNew" size="s" :loading="publishing" @click="publish">
        {{ t('admin.customization.pages.publish', {}, 'Save & publish') }}
      </CoarButton>
    </div>

    <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>
    <CoarNotice v-if="resetHint" variant="info">
      {{ t('admin.customization.pages.resetHint', {}, 'Loaded the built-in template into the editor. Nothing is saved until you click Save.') }}
    </CoarNotice>
    <CoarNotice truncate v-if="savedFlash" variant="success">
      {{ t('admin.realmSettings.saved', {}, 'Saved.') }}
    </CoarNotice>

    <div v-if="loading" class="text-sm text-gray-400">{{ t('common.loading', {}, 'Loading…') }}</div>
    <CoarPageBuilder
      v-else
      v-model="schema"
      :config="pageConfig"
      authoring-mode="code"
      class="builder"
    />
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
.revision-status { color: var(--coar-text-neutral-secondary); font-size: 0.75rem; white-space: nowrap; }
.revision-select { max-width: 190px; border: 1px solid var(--coar-border-neutral-secondary); border-radius: 0.35rem; padding: 0.3rem; background: var(--coar-background-neutral-primary); }

.builder {
  flex: 1;
  min-height: 0;
}
</style>
