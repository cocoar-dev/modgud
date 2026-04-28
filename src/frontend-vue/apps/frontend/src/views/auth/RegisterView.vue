<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { CoarButton, CoarTextInput, CoarPasswordInput, CoarCard, CoarNote } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useAuthStore } from '@/stores/auth.store'

const router = useRouter()
const authStore = useAuthStore()
const { t } = useI18n()

const userName = ref('')
const email = ref('')
const password = ref('')
const firstName = ref('')
const lastName = ref('')
const loading = ref(false)
const errorMessage = ref<string | undefined>(undefined)
const successMessage = ref<string | undefined>(undefined)

async function submit() {
  if (loading.value) return
  if (!userName.value || !email.value || !password.value) {
    errorMessage.value = t('auth.register.missingFields', {}, 'Username, email and password are required.')
    return
  }

  loading.value = true
  errorMessage.value = undefined
  successMessage.value = undefined
  try {
    await authStore.register({
      UserName: userName.value,
      Email: email.value,
      Password: password.value,
      FirstName: firstName.value || undefined,
      LastName: lastName.value || undefined,
    })
    successMessage.value = t('auth.register.success', {}, 'Account created. Check your inbox for a confirmation link before signing in.')
  } catch (err: unknown) {
    errorMessage.value = err instanceof Error ? err.message : t('auth.register.failed', {}, 'Registration failed.')
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
        <h1 class="auth-title">{{ t('auth.register.title', {}, 'Create account') }}</h1>
        <p class="auth-subtitle">{{ t('auth.register.subtitle', {}, 'Sign up to continue') }}</p>
      </div>

      <form class="auth-form" @submit.prevent="submit">
        <CoarTextInput v-model="userName" :label="t('common.username', {}, 'Username')" autocomplete="username" autofocus :disabled="loading" />
        <CoarTextInput v-model="email" :label="t('common.email', {}, 'Email')" type="email" autocomplete="email" :disabled="loading" />
        <div class="auth-row">
          <CoarTextInput v-model="firstName" :label="t('admin.users.firstName', {}, 'First name')" autocomplete="given-name" :disabled="loading" />
          <CoarTextInput v-model="lastName" :label="t('admin.users.lastName', {}, 'Last name')" autocomplete="family-name" :disabled="loading" />
        </div>
        <CoarPasswordInput v-model="password" :label="t('common.password', {}, 'Password')" autocomplete="new-password" :disabled="loading" />

        <CoarNote v-if="errorMessage" variant="error">{{ errorMessage }}</CoarNote>
        <CoarNote v-if="successMessage" variant="success">{{ successMessage }}</CoarNote>

        <CoarButton type="submit" variant="primary" :disabled="loading" :loading="loading">
          {{ t('auth.register.submit', {}, 'Create account') }}
        </CoarButton>

        <div class="auth-links">
          <a href="#" @click.prevent="router.push('/login')">{{ t('auth.register.haveAccount', {}, 'Already have an account? Sign in') }}</a>
        </div>
      </form>
    </CoarCard>
  </div>
</template>

<style scoped>
.auth-shell { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem 1rem; background: var(--coar-background-neutral-secondary); }
.auth-card { width: 100%; max-width: 440px; padding: 2rem 2rem 2.25rem; }
.auth-brand { display: flex; flex-direction: column; align-items: center; gap: 0.25rem; margin-bottom: 1.5rem; }
.auth-brand-logo { width: 48px; height: 48px; border-radius: 12px; background: var(--coar-background-accent-primary, #1f2937); color: white; display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 1.125rem; margin-bottom: 0.5rem; }
.auth-title { margin: 0; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); letter-spacing: -0.02em; }
.auth-subtitle { margin: 0.125rem 0 0 0; color: var(--coar-text-neutral-secondary); font-size: 0.875rem; }
.auth-form { display: flex; flex-direction: column; gap: 0.875rem; }
.auth-row { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; }
.auth-links { margin-top: 0.5rem; text-align: center; font-size: 0.875rem; }
.auth-links a { color: var(--coar-text-accent-primary, #2563eb); text-decoration: none; }
.auth-links a:hover { text-decoration: underline; }
</style>
