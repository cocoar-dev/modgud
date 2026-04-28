<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { CoarButton, CoarTextInput, CoarCard, CoarNote } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useAuthStore } from '@/stores/auth.store'

const router = useRouter()
const authStore = useAuthStore()
const { t } = useI18n()

const email = ref('')
const loading = ref(false)
const errorMessage = ref<string | undefined>(undefined)
const successMessage = ref<string | undefined>(undefined)

async function submit() {
  if (loading.value) return
  if (!email.value) {
    errorMessage.value = t('auth.forgotPassword.missingEmail', {}, 'Enter the email address on your account.')
    return
  }

  loading.value = true
  errorMessage.value = undefined
  try {
    await authStore.forgotPassword({ Email: email.value })
    // Backend always returns 200 for anti-enumeration — success is a generic message.
    successMessage.value = t('auth.forgotPassword.success', {}, 'If that email is on file, a reset link is on the way. Check your inbox.')
  } catch (err: unknown) {
    errorMessage.value = err instanceof Error ? err.message : t('auth.forgotPassword.failed', {}, 'Could not send reset link.')
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
        <h1 class="auth-title">{{ t('auth.forgotPassword.title', {}, 'Forgot password') }}</h1>
        <p class="auth-subtitle">{{ t('auth.forgotPassword.subtitle', {}, "We'll send you a reset link") }}</p>
      </div>

      <form class="auth-form" @submit.prevent="submit">
        <CoarTextInput v-model="email" :label="t('common.email', {}, 'Email')" type="email" autocomplete="email" autofocus :disabled="loading" />

        <CoarNote v-if="errorMessage" variant="error">{{ errorMessage }}</CoarNote>
        <CoarNote v-if="successMessage" variant="success">{{ successMessage }}</CoarNote>

        <CoarButton type="submit" variant="primary" :disabled="loading" :loading="loading">
          {{ t('auth.forgotPassword.submit', {}, 'Send reset link') }}
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
