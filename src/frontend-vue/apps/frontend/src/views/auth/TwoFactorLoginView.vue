<script setup lang="ts">
import { ref } from 'vue';
import { useRoute } from 'vue-router';
import { CoarCard, CoarButton, CoarTextInput, CoarCheckbox, CoarNote } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';
import { useAuthStore } from '@/stores/auth.store';

const route = useRoute();
const auth = useAuthStore();

const code = ref('');
const rememberMachine = ref(false);
const isLoading = ref(false);
const error = ref('');

async function onSubmit() {
  if (!code.value) return;
  isLoading.value = true;
  error.value = '';
  try {
    await authApi.twoFactorLogin({ code: code.value, rememberMachine: rememberMachine.value });
    const returnUrl = (route.query.returnUrl as string) || '/';
    await auth.completeTwoFactorLogin(returnUrl);
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Invalid code. Please try again.';
  } finally {
    isLoading.value = false;
  }
}
</script>

<template>
  <div class="auth-page">
    <div class="auth-container">
      <div class="auth-brand">
        <span class="auth-brand-icon">⚡</span>
        <span class="auth-brand-name">Cocoar Auth</span>
      </div>

      <CoarCard elevated padding="l" class="auth-card">
        <h1 class="auth-title">Two-Factor Authentication</h1>
        <p class="auth-subtitle">Enter the code from your authenticator app.</p>

        <CoarNote v-if="error" variant="error" padding="s" class="mb-4">{{ error }}</CoarNote>

        <form @submit.prevent="onSubmit">
          <div class="form-group">
            <CoarTextInput v-model="code" label="Authentication Code" placeholder="000000" autocomplete="one-time-code" :required="true" />
          </div>
          <div class="form-group">
            <CoarCheckbox v-model="rememberMachine" label="Remember this device" />
            <p class="hint-text">Checking this will skip 2FA on future logins from this browser. Do not use on shared devices.</p>
          </div>
          <CoarButton type="submit" variant="primary" :full-width="true" :loading="isLoading">
            Verify
          </CoarButton>
        </form>

        <p class="auth-footer">
          <RouterLink :to="{ path: '/login/recovery', query: route.query }" class="link">Use a recovery code instead</RouterLink>
        </p>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.auth-page { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem; }
.auth-container { display: flex; flex-direction: column; align-items: center; width: 100%; max-width: 420px; gap: 1.25rem; }
.auth-brand { display: flex; align-items: center; gap: 0.5rem; }
.auth-brand-icon { font-size: 1.5rem; line-height: 1; }
.auth-brand-name { font-size: 1.125rem; font-weight: 700; color: rgba(255,255,255,0.92); letter-spacing: -0.01em; }
.auth-card { width: 100%; }
.auth-title { margin: 0 0 0.375rem; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); letter-spacing: -0.02em; }
.auth-subtitle { margin: 0 0 1.5rem; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
.form-group { margin-bottom: 1rem; }
.mb-4 { margin-bottom: 1rem; }
.link { color: var(--coar-text-accent-primary); text-decoration: none; font-size: 0.875rem; }
.link:hover { text-decoration: underline; }
.auth-footer { margin: 1.5rem 0 0; text-align: center; }
.hint-text { margin: 0.375rem 0 0; font-size: 0.75rem; color: var(--coar-text-neutral-secondary); }
</style>
