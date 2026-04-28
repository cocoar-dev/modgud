<script setup lang="ts">
import { ref } from 'vue'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarTextInput,
  CoarFormField,
  CoarNote,
} from '@cocoar/vue-ui'

const { t, language } = useI18n()
const localization = useLocalization()!
const appConfig = useAppConfigStore()
const isPasswordless = appConfig.config.AuthenticationMinimumLevel >= 2

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

async function handleSubmit() {
  if (!userName.value.trim() || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    await http.addPath('forgot-password').post({ UserName: userName.value.trim() })
    sent.value = true
  } catch (e) {
    error.value = e instanceof HttpClientError
      ? t('auth.forgotPassword.requestFailed', {}, 'Request failed. Please try again.')
      : t('common.connectionError', {}, 'Connection to server failed.')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-surface-50 p-4 relative">
    <button
      class="absolute top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>
    <div class="w-full max-w-sm">
      <!-- Logo -->
      <div class="mb-8 text-center">
        <div class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-2xl bg-[#525e76]/10 text-[#525e76]">
          <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="h-8 w-8">
            <path d="M9 11l3 3L22 4" /><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11" />
          </svg>
        </div>
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Time<span class="text-[#525e76]">ToDo</span>
        </h1>
        <p class="mt-2 text-sm text-surface-500">{{ t('auth.forgotPassword.title', {}, 'Reset Password') }}</p>
      </div>

      <CoarCard elevated>
        <!-- Passwordless mode -->
        <div v-if="isPasswordless" class="space-y-4">
          <CoarNote variant="info">
            {{ t('auth.forgotPassword.passwordlessMode', {}, 'Password reset is not available. This application uses passwordless login.') }}
          </CoarNote>
          <RouterLink to="/login" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.forgotPassword.backToLogin', {}, 'Back to login') }}
          </RouterLink>
        </div>

        <!-- Success state -->
        <div v-else-if="sent" class="space-y-4">
          <CoarNote variant="success">
            {{ t('auth.forgotPassword.sent', {}, 'If an account exists with this username, an email with a reset link has been sent.') }}
          </CoarNote>
          <RouterLink to="/login" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
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

          <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>

          <CoarButton
            type="submit"
            :disabled="!userName.trim()"
            :loading="submitting"
            full-width
          >
            {{ t('auth.forgotPassword.sendLink', {}, 'Send link') }}
          </CoarButton>

          <RouterLink to="/login" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.forgotPassword.backToLogin', {}, 'Back to login') }}
          </RouterLink>
        </form>
      </CoarCard>
    </div>
  </div>
</template>
