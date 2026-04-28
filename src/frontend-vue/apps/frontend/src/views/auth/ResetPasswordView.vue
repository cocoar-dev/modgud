<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { CoarButton, CoarPasswordInput, CoarCard, CoarNote } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useAuthStore } from '@/stores/auth.store'

const router = useRouter()
const route = useRoute()
const authStore = useAuthStore()
const { t } = useI18n()

const userId = computed(() => {
  const v = route.query.userId
  return typeof v === 'string' ? v : ''
})
const token = computed(() => {
  const v = route.query.token
  return typeof v === 'string' ? v : ''
})

const newPassword = ref('')
const confirmPassword = ref('')
const loading = ref(false)
const errorMessage = ref<string | undefined>(undefined)
const successMessage = ref<string | undefined>(undefined)

const tokenMissing = computed(() => !userId.value || !token.value)

async function submit() {
  if (loading.value) return
  if (tokenMissing.value) {
    errorMessage.value = t('auth.resetPassword.invalidLink', {}, 'This reset link is invalid or expired.')
    return
  }
  if (!newPassword.value || !confirmPassword.value) {
    errorMessage.value = t('auth.resetPassword.missingPasswords', {}, 'Enter and confirm your new password.')
    return
  }
  if (newPassword.value !== confirmPassword.value) {
    errorMessage.value = t('auth.resetPassword.mismatch', {}, 'Passwords do not match.')
    return
  }

  loading.value = true
  errorMessage.value = undefined
  try {
    await authStore.resetPassword({
      UserId: userId.value,
      Token: token.value,
      NewPassword: newPassword.value,
    })
    successMessage.value = t('auth.resetPassword.success', {}, 'Password updated. You can sign in now.')
    setTimeout(() => router.push('/login'), 1500)
  } catch (err: unknown) {
    errorMessage.value = err instanceof Error ? err.message : t('auth.resetPassword.failed', {}, 'Could not reset password.')
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="auth-shell">
    <CoarCard class="auth-card">
      <div class="auth-brand">
        <div class="auth-brand-logo">CA</div>
        <h1 class="auth-title">{{ t('auth.resetPassword.title', {}, 'Reset password') }}</h1>
        <p class="auth-subtitle">{{ t('auth.resetPassword.subtitle', {}, 'Choose a new password to continue') }}</p>
      </div>

      <CoarNote v-if="tokenMissing" variant="error">
        {{ t('auth.resetPassword.missingParams', {}, 'Reset link is missing required parameters. Request a new one from the forgot-password page.') }}
      </CoarNote>

      <form v-else class="auth-form" @submit.prevent="submit">
        <CoarPasswordInput v-model="newPassword" :label="t('auth.resetPassword.newPassword', {}, 'New password')" autocomplete="new-password" :disabled="loading" />
        <CoarPasswordInput v-model="confirmPassword" :label="t('auth.resetPassword.confirmPassword', {}, 'Confirm new password')" autocomplete="new-password" :disabled="loading" />

        <CoarNote v-if="errorMessage" variant="error">{{ errorMessage }}</CoarNote>
        <CoarNote v-if="successMessage" variant="success">{{ successMessage }}</CoarNote>

        <CoarButton type="submit" variant="primary" :disabled="loading" :loading="loading">
          {{ t('auth.resetPassword.submit', {}, 'Reset password') }}
        </CoarButton>

        <div class="auth-links">
          <a href="#" @click.prevent="router.push('/login')">{{ t('auth.common.backToSignIn', {}, 'Back to sign in') }}</a>
        </div>
      </form>
    </CoarCard>
  </div>
</template>

<style scoped>
.auth-shell { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem 1rem; background: var(--coar-background-neutral-secondary); }
.auth-card { width: 100%; max-width: 420px; padding: 2rem 2rem 2.25rem; }
.auth-brand { display: flex; flex-direction: column; align-items: center; gap: 0.25rem; margin-bottom: 1.5rem; }
.auth-brand-logo { width: 48px; height: 48px; border-radius: 12px; background: var(--coar-background-accent-primary, #1f2937); color: white; display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 1.125rem; margin-bottom: 0.5rem; }
.auth-title { margin: 0; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); letter-spacing: -0.02em; }
.auth-subtitle { margin: 0.125rem 0 0 0; color: var(--coar-text-neutral-secondary); font-size: 0.875rem; }
.auth-form { display: flex; flex-direction: column; gap: 0.875rem; }
.auth-links { margin-top: 0.5rem; text-align: center; font-size: 0.875rem; }
.auth-links a { color: var(--coar-text-accent-primary, #2563eb); text-decoration: none; }
.auth-links a:hover { text-decoration: underline; }
</style>
