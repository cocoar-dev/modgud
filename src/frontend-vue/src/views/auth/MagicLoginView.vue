<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useLoginRedirect } from '@/composables/useLoginRedirect'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import { CoarCard, CoarFormField, CoarOtpInput, CoarButton } from '@cocoar/vue-ui'
import Notice from '@/components/Notice.vue'

const route = useRoute()
const authStore = useAuthStore()

// The emailed magic-link URL carries the pending continuation as ?redirect=
// (validated server-side, same-origin-guarded again here) — finish through
// the shared logic so a /connect/authorize target resumes the client app's
// OIDC flow instead of hardcoding the dashboard.
const { finishLogin } = useLoginRedirect()
const { t, language } = useI18n()
const localization = useLocalization()!

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const status = ref<'loading' | 'mfa' | 'success' | 'error'>('loading')
const errorMessage = ref('')
const totpCode = ref('')
const submitting = ref(false)

onMounted(async () => {
  const userId = route.query.userId as string
  const token = route.query.token as string

  if (!userId || !token) {
    status.value = 'error'
    errorMessage.value = t('auth.magicLogin.invalidLink', {}, 'Invalid login link.')
    return
  }

  try {
    const result = await authStore.magicLinkLogin(userId, token)
    // TOTP-protected accounts: the magic-link proves mailbox control but is not
    // a 2FA bypass — finish with the authenticator code before signing in.
    if (result?.RequiresMfa) {
      status.value = 'mfa'
      return
    }
    status.value = 'success'
    await finishLogin()
  } catch {
    status.value = 'error'
    errorMessage.value = t('auth.magicLogin.expiredLink', {}, 'This login link is invalid or expired.')
  }
})

async function submitTotp() {
  if (!totpCode.value.trim() || submitting.value) return
  submitting.value = true
  errorMessage.value = ''
  try {
    await authStore.mfaLogin(totpCode.value.replace(/[\s-]/g, ''))
    status.value = 'success'
    await finishLogin()
  } catch {
    errorMessage.value = t('auth.mfa.invalidCode', {}, 'Invalid code. Please try again.')
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
    <div class="w-full max-w-sm text-center">
      <div class="mb-8">
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Modgud
        </h1>
      </div>

      <!-- Loading -->
      <div v-if="status === 'loading'" class="space-y-4">
        <p class="text-surface-500">{{ t('auth.magicLogin.loggingIn', {}, 'Logging in...') }}</p>
      </div>

      <!-- TOTP step-up (account has an authenticator enabled) -->
      <CoarCard v-else-if="status === 'mfa'" elevated>
        <form class="space-y-4" @submit.prevent="submitTotp">
          <p class="text-sm text-surface-500">
            {{ t('auth.mfa.totpSubtitle', {}, 'Enter the code from your authenticator app.') }}
          </p>
          <CoarFormField :label="t('auth.mfa.authenticatorCode', {}, 'Authenticator Code')">
            <CoarOtpInput v-model="totpCode" type="numeric" :length="6" auto-focus required />
          </CoarFormField>
          <Notice v-if="errorMessage" variant="error">{{ errorMessage }}</Notice>
          <CoarButton type="submit" :disabled="!totpCode.trim()" :loading="submitting" full-width>
            {{ t('common.confirm', {}, 'Confirm') }}
          </CoarButton>
        </form>
      </CoarCard>

      <!-- Error -->
      <div v-else-if="status === 'error'" class="space-y-4">
        <p class="text-red-600">{{ errorMessage }}</p>
        <RouterLink
          :to="{ path: '/login', query: { redirect: route.query.redirect } }"
          class="inline-block rounded bg-[#525e76] px-4 py-2 text-sm font-medium text-white transition hover:bg-[#434d61]"
        >
          {{ t('auth.magicLogin.goToLogin', {}, 'Go to login') }}
        </RouterLink>
      </div>
    </div>
  </div>
</template>
