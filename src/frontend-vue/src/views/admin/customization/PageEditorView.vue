<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  CoarPageBuilder,
  type PageNode,
  type PageConfig,
} from '@cocoar/vue-page-builder'
import { CoarButton, CoarNote, useDialog } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import AssetPicker from '@/components/AssetPicker.vue'
import type { AssetDto } from '@/models/assets'

const { t, language } = useI18n()
const ui = useUI()
const route = useRoute()
const router = useRouter()
const dialog = useDialog()

const slug = computed(() => (route.params.slug as string) ?? '')

// Per-slot action list. Each slot exposes a different set of auth
// actions a button can wire up to. Page-builder only uses these to
// populate the dropdown — the runtime renderer is the real boundary
// and won't fire an action whose id isn't in its handlers map.
const actionsBySlot: Record<string, { id: string; label: string }[]> = {
  'login': [
    { id: 'auth:login',           label: 'Sign in' },
    { id: 'auth:passkey',         label: 'Sign in with passkey' },
    { id: 'auth:magic-link',      label: 'Send magic link' },
    { id: 'auth:forgot-password', label: 'Forgot password' },
    { id: 'auth:register',        label: 'Create account' },
    { id: 'auth:mfa-totp',        label: 'Enter authenticator code' },
    { id: 'auth:mfa-email-otp',   label: 'Enter email code' },
  ],
  'logout': [
    { id: 'auth:back-to-login',   label: 'Sign in again' },
  ],
  'password-forgot': [
    { id: 'auth:send-reset-link', label: 'Send reset link' },
    { id: 'auth:back-to-login',   label: 'Back to login' },
  ],
}

const pageConfig = computed<PageConfig>(() => ({
  allowedElements: [
    'stack', 'card', 'section', 'divider',
    'heading', 'paragraph',
    'text-input', 'checkbox', 'button', 'link', 'image',
  ],
  availableActions: actionsBySlot[slug.value] ?? [],
  assetResolver: (id: string) => `/api/assets/${id}`,
  async pickAsset(currentId?: string) {
    const ref$ = dialog.open<AssetDto>(AssetPicker, {
      title: t('asset.picker.title', {}, 'Select an asset'),
      size: 'l',
    }, { selectedId: currentId ?? null })
    const result = await ref$.result
    return result?.Id ?? null
  },
}))

const labelBySlot: Record<string, string> = {
  'login': t('admin.customization.pages.login.title', {}, 'Login'),
  'logout': t('admin.customization.pages.logout.title', {}, 'Logout'),
  'password-forgot': t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password'),
}

watch([language, slug], () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Plattform')
  // String subtitle to match the app-wide header model (UI/UX wave 4, #13);
  // the page hierarchy stays as text ("Pages · <name>"). The parent-list link
  // the breadcrumb gave is still covered by the in-page Back button + sidebar.
  ctx.header.subTitle = `${t('admin.customization.pages.title', {}, 'Pages')} · ${labelBySlot[slug.value] ?? slug.value}`
  ctx.header.icon = 'layout-template'
  ctx.content.container = false
}), { immediate: true })

function emptyTree(): PageNode {
  return {
    id: 'root',
    type: 'page',
    style: { gap: '16px', padding: '24px' },
    children: [],
  } as PageNode
}

const schema = ref<PageNode>(emptyTree())
const loading = ref(true)
const saving = ref(false)
const savedFlash = ref(false)
const error = ref<string | null>(null)

async function loadSchema() {
  loading.value = true
  error.value = null
  try {
    const res = await fetch(`/api/admin/customization/pages/${encodeURIComponent(slug.value)}`, {
      headers: { Accept: 'application/json' },
    })
    if (!res.ok) {
      error.value = `Failed to load (HTTP ${res.status})`
      return
    }
    const body = await res.json() as { Slug: string; Schema: string | null }
    if (body.Schema) {
      try {
        schema.value = JSON.parse(body.Schema) as PageNode
      } catch (e: any) {
        error.value = `Stored schema is invalid JSON: ${e?.message ?? e}`
        schema.value = emptyTree()
      }
    } else {
      schema.value = emptyTree()
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
    const res = await fetch(`/api/admin/customization/pages/${encodeURIComponent(slug.value)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ Schema: JSON.stringify(schema.value) }),
    })
    if (!res.ok) {
      const body = await res.json().catch(() => null) as { Message?: string } | null
      error.value = body?.Message ?? `Save failed (HTTP ${res.status})`
      return
    }
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1500)
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
    const res = await fetch(`/api/admin/customization/pages/${encodeURIComponent(slug.value)}`, {
      method: 'DELETE',
      headers: { Accept: 'application/json' },
    })
    if (!res.ok) {
      error.value = `Reset failed (HTTP ${res.status})`
      return
    }
    schema.value = emptyTree()
    savedFlash.value = true
    setTimeout(() => { savedFlash.value = false }, 1500)
  } catch (e: any) {
    error.value = e?.message ?? String(e)
  } finally {
    saving.value = false
  }
}

function back() {
  router.push('/plattform/customization/pages')
}

onMounted(loadSchema)
</script>

<template>
  <div class="editor-page">
    <div class="editor-toolbar">
      <CoarButton size="s" variant="ghost" icon-start="arrow-left" @click="back">
        {{ t('common.back', {}, 'Back') }}
      </CoarButton>
      <div class="toolbar-spacer" />
      <CoarButton size="s" variant="ghost" :loading="saving" @click="resetToDefault">
        {{ t('admin.customization.pages.reset', {}, 'Reset to default') }}
      </CoarButton>
      <CoarButton size="s" :loading="saving" @click="save">
        {{ t('common.save', {}, 'Save') }}
      </CoarButton>
    </div>

    <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
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
