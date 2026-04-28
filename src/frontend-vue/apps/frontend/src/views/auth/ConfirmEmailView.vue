<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { CoarButton, CoarCard, CoarNote, CoarSpinner } from '@cocoar/vue-ui'
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

type Status = 'pending' | 'success' | 'error' | 'invalid'
const status = ref<Status>('pending')
const errorMessage = ref<string | undefined>(undefined)

onMounted(async () => {
  if (!userId.value || !token.value) {
    status.value = 'invalid'
    return
  }
  try {
    await authStore.confirmEmail(userId.value, token.value)
    status.value = 'success'
  } catch (err: unknown) {
    status.value = 'error'
    errorMessage.value = err instanceof Error ? err.message : t('auth.confirmEmail.failed', {}, 'Email confirmation failed.')
  }
})
</script>

<template>
  <div class="auth-shell">
    <CoarCard class="auth-card">
      <div class="auth-brand">
        <div class="auth-brand-logo">CA</div>
        <h1 class="auth-title">{{ t('auth.confirmEmail.title', {}, 'Email confirmation') }}</h1>
      </div>

      <div v-if="status === 'pending'" class="state-row">
        <CoarSpinner />
        <span>{{ t('auth.confirmEmail.pending', {}, 'Confirming your email…') }}</span>
      </div>

      <CoarNote v-else-if="status === 'success'" variant="success">
        {{ t('auth.confirmEmail.success', {}, 'Your email is confirmed. You can sign in now.') }}
      </CoarNote>

      <CoarNote v-else-if="status === 'invalid'" variant="error">
        {{ t('auth.confirmEmail.invalid', {}, 'This confirmation link is missing required parameters.') }}
      </CoarNote>

      <CoarNote v-else variant="error">
        {{ errorMessage ?? t('auth.confirmEmail.failed', {}, 'Email confirmation failed.') }}
      </CoarNote>

      <div class="auth-actions">
        <CoarButton variant="primary" @click="router.push('/login')">
          {{ t('auth.confirmEmail.goToSignIn', {}, 'Go to sign in') }}
        </CoarButton>
      </div>
    </CoarCard>
  </div>
</template>

<style scoped>
.auth-shell { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem 1rem; background: var(--coar-background-neutral-secondary); }
.auth-card { width: 100%; max-width: 420px; padding: 2rem 2rem 2.25rem; display: flex; flex-direction: column; gap: 1rem; }
.auth-brand { display: flex; flex-direction: column; align-items: center; gap: 0.25rem; }
.auth-brand-logo { width: 48px; height: 48px; border-radius: 12px; background: var(--coar-background-accent-primary, #1f2937); color: white; display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 1.125rem; margin-bottom: 0.5rem; }
.auth-title { margin: 0; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); letter-spacing: -0.02em; }
.state-row { display: flex; align-items: center; justify-content: center; gap: 0.5rem; color: var(--coar-text-neutral-secondary); padding: 1rem 0; }
.auth-actions { display: flex; justify-content: center; }
</style>
