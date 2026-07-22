<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { CoarPageBuilder, type PageNode } from '@cocoar/vue-page-builder'
import { CoarButton, CoarNote, useDialog } from '@cocoar/vue-ui'
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

const { t, language } = useI18n()
const ui = useUI()
const route = useRoute()
const router = useRouter()
const dialog = useDialog()

const slug = computed(() => (route.params.slug as string) ?? '')
const slot = computed<AuthPageSlot>(() =>
  AUTH_PAGE_SLOTS.includes(slug.value as AuthPageSlot)
    ? slug.value as AuthPageSlot
    : 'login')
const applicationId = computed(() => typeof route.query.appId === 'string' ? route.query.appId : null)
const applicationName = computed(() => typeof route.query.appName === 'string' ? route.query.appName : null)
const isApplicationPage = computed(() => !!applicationId.value)
const endpoint = computed(() => isApplicationPage.value
  ? `/api/app/${encodeURIComponent(applicationId.value!)}/pages/${encodeURIComponent(slot.value)}`
  : `/api/admin/customization/pages/${encodeURIComponent(slot.value)}`)

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

watch([language, slug, applicationName], () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  const scope = applicationName.value ?? t('admin.customization.pages.title', {}, 'Pages')
  ctx.header.subTitle = `${scope} · ${labelBySlot[slug.value] ?? slug.value}`
  ctx.header.icon = 'layout-template'
  ctx.content.container = false
}), { immediate: true })

const schema = ref<PageNode>(createDefaultAuthPageSchema(slot.value))
const loading = ref(true)
const saving = ref(false)
const savedFlash = ref(false)
const error = ref<string | null>(null)
const inheritsRealm = ref(false)

async function loadSchema() {
  loading.value = true
  error.value = null
  try {
    const res = await fetch(endpoint.value, { headers: { Accept: 'application/json' } })
    if (!res.ok) {
      error.value = `Failed to load (HTTP ${res.status})`
      return
    }
    const body = await res.json() as {
      Slug: string
      Schema: string | null
      EffectiveSchema?: string | null
      InheritsRealm?: boolean
    }
    inheritsRealm.value = body.InheritsRealm ?? false
    const effectiveSchema = body.Schema ?? body.EffectiveSchema
    if (!effectiveSchema) {
      schema.value = createDefaultAuthPageSchema(slot.value)
      return
    }
    try {
      schema.value = JSON.parse(effectiveSchema) as PageNode
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
  saving.value = true
  error.value = null
  try {
    const res = await fetch(endpoint.value, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ Schema: JSON.stringify(schema.value) }),
    })
    if (!res.ok) {
      const body = await res.json().catch(() => null) as { Message?: string } | null
      error.value = body?.Message ?? `Save failed (HTTP ${res.status})`
      return
    }
    inheritsRealm.value = false
    flashSaved()
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

async function resetToDefault() {
  saving.value = true
  error.value = null
  try {
    const res = await fetch(endpoint.value, { method: 'DELETE', headers: { Accept: 'application/json' } })
    if (!res.ok) {
      error.value = `Reset failed (HTTP ${res.status})`
      return
    }
    await loadSchema()
    flashSaved()
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

function flashSaved() {
  savedFlash.value = true
  setTimeout(() => { savedFlash.value = false }, 1500)
}

function back() {
  if (isApplicationPage.value) {
    router.back()
    return
  }
  router.push('/platform/customization/pages')
}

onMounted(loadSchema)
watch([slot, applicationId], loadSchema)
</script>

<template>
  <div class="editor-page">
    <div class="editor-toolbar">
      <CoarButton size="s" variant="ghost" icon-start="arrow-left" @click="back">
        {{ t('common.back', {}, 'Back') }}
      </CoarButton>
      <div class="toolbar-spacer" />
      <CoarButton size="s" variant="ghost" :loading="saving" @click="resetToDefault">
        {{ isApplicationPage
          ? t('admin.customization.pages.inherit', {}, 'Inherit realm page')
          : t('admin.customization.pages.reset', {}, 'Reset to default') }}
      </CoarButton>
      <CoarButton size="s" :loading="saving" @click="save">
        {{ t('common.save', {}, 'Save') }}
      </CoarButton>
    </div>

    <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
    <CoarNote v-if="isApplicationPage && inheritsRealm" variant="info">
      {{ t('admin.customization.pages.inheritsRealm', {}, 'This application currently uses the realm page. Saving creates an application-specific override.') }}
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

.toolbar-spacer { flex: 1; }

.builder {
  flex: 1;
  min-height: 0;
}
</style>
