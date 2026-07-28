<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarPasswordInput,
  CoarFormField,
} from '@cocoar/vue-ui'
import Notice from '@/components/Notice.vue'

const { t, language } = useI18n()
const localization = useLocalization()!

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const route = useRoute()
const router = useRouter()
const http = useHttpClient('/api/account')

const userId = computed(() => route.query.userId as string)
const token = computed(() => route.query.token as string)
const isValid = computed(() => !!userId.value && !!token.value)

const newPassword = ref('')
const confirmPassword = ref('')
const submitting = ref(false)
const success = ref(false)
const error = ref('')

const passwordsMatch = computed(() => newPassword.value === confirmPassword.value)

async function handleSubmit() {
  if (!newPassword.value || !passwordsMatch.value || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    await http.addPath('reset-password').post({
      UserId: userId.value,
      Token: token.value,
      NewPassword: newPassword.value,
    })
    success.value = true
  } catch (e) {
    if (e instanceof HttpClientError) {
      const body = e.body as any
      error.value = body?.Message ?? t('auth.resetPassword.failed', {}, 'Reset failed.')
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
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
          Modgud
        </h1>
        <p class="mt-2 text-sm text-surface-500">{{ t('auth.resetPassword.title', {}, 'Set New Password') }}</p>
      </div>

      <CoarCard elevated>
        <!-- Passwordless mode -->
        <div v-if="useAppConfigStore().config.AuthenticationMinimumLevel >= 2" class="space-y-4">
          <Notice variant="info">
            {{ t('auth.resetPassword.passwordlessMode', {}, 'Password reset is not available. This application uses passwordless login.') }}
          </Notice>
          <RouterLink :to="{ path: '/login', query: { redirect: route.query.redirect } }" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.resetPassword.backToLogin', {}, 'Back to login') }}
          </RouterLink>
        </div>

        <!-- Invalid link -->
        <div v-else-if="!isValid" class="space-y-4">
          <Notice variant="error">
            {{ t('auth.resetPassword.invalidLink', {}, 'Invalid link. Please request a new link.') }}
          </Notice>
          <RouterLink to="/forgot-password" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.resetPassword.requestNewLink', {}, 'Request new link') }}
          </RouterLink>
        </div>

        <!-- Success -->
        <div v-else-if="success" class="space-y-4">
          <Notice variant="success">
            {{ t('auth.resetPassword.success', {}, 'Password has been successfully reset. You can now sign in.') }}
          </Notice>
          <CoarButton full-width @click="router.push({ path: '/login', query: { redirect: route.query.redirect } })">{{ t('auth.resetPassword.goToLogin', {}, 'Go to login') }}</CoarButton>
        </div>

        <!-- Form -->
        <form v-else class="space-y-4" @submit.prevent="handleSubmit">
          <CoarFormField :label="t('auth.resetPassword.newPassword', {}, 'New Password')">
            <CoarPasswordInput
              v-model="newPassword"
              :placeholder="t('auth.resetPassword.newPassword', {}, 'New Password')"
              autocomplete="new-password"
              required
            />
          </CoarFormField>

          <CoarFormField :label="t('auth.resetPassword.confirmPassword', {}, 'Confirm Password')">
            <CoarPasswordInput
              v-model="confirmPassword"
              :placeholder="t('auth.resetPassword.confirmPlaceholder', {}, 'Repeat password')"
              autocomplete="new-password"
              required
            />
          </CoarFormField>

          <Notice v-if="confirmPassword && !passwordsMatch" variant="error">
            {{ t('auth.resetPassword.passwordMismatch', {}, 'Passwords do not match.') }}
          </Notice>

          <Notice v-if="error" variant="error">{{ error }}</Notice>

          <CoarButton
            type="submit"
            :disabled="!newPassword || !passwordsMatch"
            :loading="submitting"
            full-width
          >
            {{ t('auth.resetPassword.submit', {}, 'Set password') }}
          </CoarButton>
        </form>
      </CoarCard>
    </div>
  </div>
</template>
