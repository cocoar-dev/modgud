<script setup lang="ts">
import { computed, onMounted, provide, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import { CoarButton, CoarCard } from '@cocoar/vue-ui'
import {
  CoarPageRenderer,
  normalizePageSchema,
  type ActionHandler,
  type PageNode,
} from '@cocoar/vue-page-builder'
import { useAppConfigStore } from '@/stores/appconfig.store'
import AuthBrand from '@/components/auth/AuthBrand.vue'
import { createAuthPageConfig } from '@/page-builder/authPageConfig'
import { LOGIN_PAGE_RUNTIME_KEY } from '@/page-builder/loginPageRuntime'

const { t, language } = useI18n()
const localization = useLocalization()!
const route = useRoute()
const router = useRouter()
const appConfig = useAppConfigStore()
const branding = computed(() => appConfig.config.Branding)

provide(LOGIN_PAGE_RUNTIME_KEY, {
  branding,
  externalLogins: ref([]),
  startExternalLogin: () => {},
})

const pageConfig = createAuthPageConfig('logout')
const schema = ref<PageNode | null>(null)
const ready = ref(false)
const actions: Record<string, ActionHandler> = {
  'auth:back-to-login': () => router.push('/login'),
}

onMounted(async () => {
  try {
    await appConfig.load()
    if (!appConfig.config.Features.PageBuilder || route.query.safemode === '1') return
    const stored = appConfig.config.Pages.logout
    if (!stored) return
    schema.value = normalizePageSchema(JSON.parse(stored), { elements: pageConfig.elements }).schema
  } catch {
    schema.value = null
  } finally {
    ready.value = true
  }
})

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}
</script>

<template>
  <main class="min-h-screen bg-surface-50 relative">
    <button
      class="absolute z-10 top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>

    <div v-if="!ready" class="flex min-h-screen items-center justify-center text-sm text-surface-400">
      {{ t('common.loading', {}, 'Loading…') }}
    </div>

    <CoarPageRenderer
      v-else-if="schema"
      :schema="schema"
      :config="pageConfig"
      :actions="actions"
    />

    <div v-else class="flex min-h-screen items-center justify-center p-4">
      <div class="w-full max-w-sm text-center">
        <AuthBrand class="mb-6" spacing="compact" />
        <CoarCard elevated class="space-y-4">
          <h1 class="text-2xl font-bold tracking-tight text-surface-800">
            {{ t('logout.signedOut', {}, 'Signed out') }}
          </h1>
          <p class="text-sm text-surface-500">
            {{ t('logout.signedOutHint', {}, 'Your session has ended safely.') }}
          </p>
          <CoarButton full-width @click="router.push('/login')">
            {{ t('logout.signInAgain', {}, 'Sign in again') }}
          </CoarButton>
        </CoarCard>
      </div>
    </div>
  </main>
</template>
