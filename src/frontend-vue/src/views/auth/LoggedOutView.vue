<script setup lang="ts">
import { computed, onMounted, provide, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import { CoarButton, CoarCard } from '@cocoar/vue-ui'
import {
  normalizePageSchema,
  type ActionHandler,
  type PageNode,
} from '@cocoar/vue-page-builder'
import { useAppConfigStore } from '@/stores/appconfig.store'
import AuthBrand from '@/components/auth/AuthBrand.vue'
import {
  authPageLocale,
  createAuthPageConfig,
  createDefaultAuthPageSchema,
} from '@/page-builder/authPageConfig'
import { createAuthRuntimeContext } from '@/page-builder/authPageContext'
import AuthRuntimePageRenderer from '@/page-builder/AuthRuntimePageRenderer.vue'
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

const pageConfig = computed(() => createAuthPageConfig(
  'logout',
  authPageLocale(language.value),
  undefined,
  appConfig.config.PageTheme,
))
const fallbackSchema = computed(() => createDefaultAuthPageSchema('logout'))
const schema = ref<PageNode | null>(null)
const ready = ref(false)
const runtimeContext = computed(() => createAuthRuntimeContext({
  config: appConfig.config,
  viewState: 'complete',
}))
const actions: Record<string, ActionHandler> = {
  'auth:back-to-login': () => router.push('/login'),
  'legal:terms': () => {
    if (appConfig.config.Legal.TermsOfServiceUrl) window.location.assign(appConfig.config.Legal.TermsOfServiceUrl)
  },
  'legal:privacy': () => {
    if (appConfig.config.Legal.PrivacyPolicyUrl) window.location.assign(appConfig.config.Legal.PrivacyPolicyUrl)
  },
}

onMounted(async () => {
  try {
    await appConfig.load()
    if (!appConfig.config.Features.PageBuilder || route.query.safemode === '1') return
    const stored = appConfig.config.Pages.logout
    if (!stored) return
    schema.value = normalizePageSchema(JSON.parse(stored), { elements: pageConfig.value.elementTypes }).schema
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

    <AuthRuntimePageRenderer
      v-else-if="schema"
      page-id="auth-logout"
      :schema="schema"
      :config="pageConfig"
      :actions="actions"
      :fallback-schema="fallbackSchema"
      :runtime-context="runtimeContext"
      :locale="authPageLocale(language)"
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
