<script setup lang="ts">
import { computed, onMounted, provide, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { isSameOriginPath } from '@/composables/useLoginRedirect'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarNotice,
  CoarCard,
  CoarButton,
  CoarTextInput,
  CoarFormField,
} from '@cocoar/vue-ui'
import {
  normalizePageSchema,
  type ActionHandler,
  type ActionValues,
  type PageNode,
} from '@cocoar/vue-page-builder'
import {
  authPageLocale,
  createAuthPageConfig,
  createDefaultAuthPageSchema,
} from '@/page-builder/authPageConfig'
import { createAuthRuntimeContext } from '@/page-builder/authPageContext'
import AuthRuntimePageRenderer from '@/page-builder/AuthRuntimePageRenderer.vue'
import { LOGIN_PAGE_RUNTIME_KEY } from '@/page-builder/loginPageRuntime'
import AuthBrand from '@/components/auth/AuthBrand.vue'

const { t, language } = useI18n()
const localization = useLocalization()!
const appConfig = useAppConfigStore()
const branding = computed(() => appConfig.config.Branding)
const isPasswordless = computed(() => appConfig.config.AuthenticationMinimumLevel >= 2)

// Forwarded on every "Back to login" link so a pending continuation
// (e.g. a client app's /connect/authorize flow) survives the detour.
const route = useRoute()
const router = useRouter()
const redirectTarget = computed(() => isSameOriginPath(route.query.redirect) ? route.query.redirect : '/')

provide(LOGIN_PAGE_RUNTIME_KEY, {
  branding,
  externalLogins: ref([]),
  startExternalLogin: () => {},
})

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const http = useHttpClient('/api/account')

const userName = ref('')
const submitting = ref(false)
const sent = ref(false)
const error = ref('')

const forgotPageConfig = computed(() => createAuthPageConfig('password-forgot', authPageLocale(language.value)))
const forgotFallbackSchema = computed(() => createDefaultAuthPageSchema('password-forgot'))
const customForgotSchema = ref<PageNode | null>(null)
const forgotPageReady = ref(false)

const forgotViewState = computed(() => isPasswordless.value
  ? 'passwordless-unavailable'
  : sent.value
    ? 'accepted'
    : submitting.value
      ? 'submitting'
      : error.value
        ? 'error'
        : 'form')
const forgotRuntimeContext = computed(() => createAuthRuntimeContext({
  config: appConfig.config,
  viewState: forgotViewState.value,
  feedbackMessage: sent.value
    ? t('auth.forgotPassword.sent', {}, 'If an account exists, a message has been sent.')
    : error.value,
  feedbackSuccess: sent.value,
}))
onMounted(async () => {
  try {
    await appConfig.loadForLogin(redirectTarget.value)
    if (!appConfig.config.Features.PageBuilder || route.query.safemode === '1') return
    const stored = appConfig.config.Pages['password-forgot']
    if (!stored) return
    customForgotSchema.value = normalizePageSchema(
      JSON.parse(stored),
      { elements: forgotPageConfig.value.elements },
    ).schema
  } catch {
    customForgotSchema.value = null
  } finally {
    forgotPageReady.value = true
  }
})

function forgotErrorMessage(e: unknown): string {
  return e instanceof HttpClientError
    ? t('auth.forgotPassword.requestFailed', {}, 'Request failed. Please try again.')
    : t('common.connectionError', {}, 'Connection to server failed.')
}

async function requestReset(name: string) {
  try {
    // Thread the pending continuation (e.g. a client app's /connect/authorize
    // flow) through the e-mail round trip; the backend re-validates it and the
    // reset page forwards ?redirect= to /login on success.
    const redirect = route.query.redirect
    await http.addPath('forgot-password').post({
      UserName: name.trim(),
      ReturnUrl: isSameOriginPath(redirect) ? redirect : null,
    })
    sent.value = true
  } catch (e) {
    throw new Error(forgotErrorMessage(e))
  }
}

async function handleSubmit() {
  if (!userName.value.trim() || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    await requestReset(userName.value)
  } catch (e) {
    error.value = e instanceof Error ? e.message : forgotErrorMessage(e)
  } finally {
    submitting.value = false
  }
}

function requiredUsername(values: ActionValues): string {
  const value = values.username
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error(t('auth.forgotPassword.usernameRequired', {}, 'Enter your username or email address.'))
  }
  return value
}

const customForgotActions: Record<string, ActionHandler> = {
  'auth:send-reset-link': async (values) => {
    userName.value = requiredUsername(values)
    await requestReset(userName.value)
  },
  'auth:back-to-login': () => router.push({ path: '/login', query: { redirect: route.query.redirect } }),
  'legal:terms': () => {
    if (appConfig.config.Legal.TermsOfServiceUrl) window.location.assign(appConfig.config.Legal.TermsOfServiceUrl)
  },
  'legal:privacy': () => {
    if (appConfig.config.Legal.PrivacyPolicyUrl) window.location.assign(appConfig.config.Legal.PrivacyPolicyUrl)
  },
}
</script>

<template>
  <div class="min-h-screen bg-surface-50 relative">
    <button
      class="absolute z-10 top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>

    <div v-if="!forgotPageReady" class="flex min-h-screen items-center justify-center text-sm text-surface-400">
      {{ t('common.loading', {}, 'Loading…') }}
    </div>

    <AuthRuntimePageRenderer
      v-else-if="customForgotSchema && !isPasswordless && !sent"
      page-id="auth-password-forgot"
      :schema="customForgotSchema"
      :config="forgotPageConfig"
      :actions="customForgotActions"
      :fallback-schema="forgotFallbackSchema"
      :runtime-context="forgotRuntimeContext"
      :view-state="forgotViewState"
      :locale="authPageLocale(language)"
    />

    <div v-else class="flex min-h-screen items-center justify-center p-4">
      <div class="w-full max-w-sm">
      <!-- Logo -->
      <div class="mb-8 text-center">
        <AuthBrand spacing="compact" />
        <p class="mt-2 text-sm text-surface-500">{{ t('auth.forgotPassword.title', {}, 'Reset Password') }}</p>
      </div>

      <CoarCard elevated>
        <!-- Passwordless mode -->
        <div v-if="isPasswordless" class="space-y-4">
          <CoarNotice variant="info">
            {{ t('auth.forgotPassword.passwordlessMode', {}, 'Password reset is not available. This application uses passwordless login.') }}
          </CoarNotice>
          <RouterLink :to="{ path: '/login', query: { redirect: route.query.redirect } }" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.forgotPassword.backToLogin', {}, 'Back to login') }}
          </RouterLink>
        </div>

        <!-- Success state -->
        <div v-else-if="sent" class="space-y-4">
          <CoarNotice variant="success">
            {{ t('auth.forgotPassword.sent', {}, 'If an account exists with this username, an email with a reset link has been sent.') }}
          </CoarNotice>
          <RouterLink :to="{ path: '/login', query: { redirect: route.query.redirect } }" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.forgotPassword.backToLogin', {}, 'Back to login') }}
          </RouterLink>
        </div>

        <!-- Form -->
        <form v-else class="space-y-4" @submit.prevent="handleSubmit">
          <p class="text-sm text-surface-600">
            {{ t('auth.forgotPassword.instructions', {}, 'Enter your username or email address. You will receive a link to reset your password.') }}
          </p>

          <CoarFormField :label="t('auth.forgotPassword.usernameOrEmail', {}, 'Username or Email')">
            <CoarTextInput
              v-model="userName"
              :placeholder="t('auth.forgotPassword.usernameOrEmail', {}, 'Username or Email')"
              autocomplete="username"
              required
            />
          </CoarFormField>

          <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

          <CoarButton
            type="submit"
            :disabled="!userName.trim()"
            :loading="submitting"
            full-width
          >
            {{ t('auth.forgotPassword.sendLink', {}, 'Send link') }}
          </CoarButton>

          <RouterLink :to="{ path: '/login', query: { redirect: route.query.redirect } }" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.forgotPassword.backToLogin', {}, 'Back to login') }}
          </RouterLink>
        </form>
      </CoarCard>
      </div>
    </div>
  </div>
</template>
