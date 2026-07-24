<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarPasswordInput,
  CoarFormField,
} from '@cocoar/vue-ui'
import AppNote from '@/components/AppNote.vue'

// First-admin bootstrap form (C15b). Recipient lands here from the
// magic-link in the bootstrap email (or printed on stdout by the CLI).
// Sets a password → atomic user+role+group seed on the backend → auto-login.

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

const token = computed(() => route.query.token as string)
const isValid = computed(() => !!token.value)

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
    await http.addPath('bootstrap-admin').post({
      Token: token.value,
      Password: newPassword.value,
    })
    success.value = true
    // Backend auto-signs the user in via the cookie scheme. Land them on
    // the dashboard after a short success beat so they see the success
    // note.
    setTimeout(() => router.push('/dashboard'), 800)
  } catch (e) {
    if (e instanceof HttpClientError) {
      const body = e.body as any
      error.value = body?.detail ?? body?.Message ?? t('auth.bootstrap.failed', {}, 'Bootstrap failed.')
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
            <path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2" /><circle cx="9" cy="7" r="4" /><path d="M22 11l-3 3-2-2" />
          </svg>
        </div>
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Modgud
        </h1>
        <p class="mt-2 text-sm text-surface-500">
          {{ t('auth.bootstrap.title', {}, 'Set up Admin Access') }}
        </p>
      </div>

      <CoarCard elevated>
        <!-- Invalid link -->
        <div v-if="!isValid" class="space-y-4">
          <AppNote variant="error" :truncate="false">
            {{ t('auth.bootstrap.invalidLink', {}, 'Invalid bootstrap link. Ask your administrator to issue a new invite.') }}
          </AppNote>
          <RouterLink to="/login" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.bootstrap.backToLogin', {}, 'Back to login') }}
          </RouterLink>
        </div>

        <!-- Success -->
        <div v-else-if="success" class="space-y-4">
          <AppNote variant="success" :truncate="false">
            {{ t('auth.bootstrap.success', {}, 'Your admin account has been created. Redirecting to the dashboard…') }}
          </AppNote>
        </div>

        <!-- Form -->
        <form v-else class="space-y-4" @submit.prevent="handleSubmit">
          <AppNote variant="info" :truncate="false">
            {{ t('auth.bootstrap.intro', {}, 'Set a password to activate your admin account. This link is single-use and expires in 7 days.') }}
          </AppNote>

          <CoarFormField :label="t('auth.bootstrap.newPassword', {}, 'New Password')">
            <CoarPasswordInput
              v-model="newPassword"
              :placeholder="t('auth.bootstrap.newPassword', {}, 'New Password')"
              autocomplete="new-password"
              required
            />
          </CoarFormField>

          <CoarFormField :label="t('auth.bootstrap.confirmPassword', {}, 'Confirm Password')">
            <CoarPasswordInput
              v-model="confirmPassword"
              :placeholder="t('auth.bootstrap.confirmPlaceholder', {}, 'Repeat password')"
              autocomplete="new-password"
              required
            />
          </CoarFormField>

          <AppNote v-if="confirmPassword && !passwordsMatch" variant="error" :truncate="false">
            {{ t('auth.bootstrap.passwordMismatch', {}, 'Passwords do not match.') }}
          </AppNote>

          <AppNote v-if="error" variant="error" :truncate="false">{{ error }}</AppNote>

          <CoarButton
            type="submit"
            :disabled="!newPassword || !passwordsMatch"
            :loading="submitting"
            full-width
          >
            {{ t('auth.bootstrap.submit', {}, 'Set password & sign in') }}
          </CoarButton>
        </form>
      </CoarCard>
    </div>
  </div>
</template>
