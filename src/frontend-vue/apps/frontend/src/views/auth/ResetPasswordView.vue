<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { CoarCard, CoarButton, CoarPasswordInput, CoarNote } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';

const route = useRoute();
const router = useRouter();

const email = (route.query.email as string) || '';
const token = (route.query.token as string) || '';
const newPassword = ref('');
const confirmPassword = ref('');
const isLoading = ref(false);
const error = ref('');
const success = ref(false);

onMounted(() => {
  if (!email || !token) {
    error.value = 'This reset link is invalid or has expired. Please request a new one.';
  }
});

async function onSubmit() {
  if (!email || !token || !newPassword.value) return;
  if (newPassword.value !== confirmPassword.value) {
    error.value = 'Passwords do not match.';
    return;
  }
  isLoading.value = true;
  error.value = '';
  try {
    await authApi.resetPassword({ email, token, newPassword: newPassword.value });
    success.value = true;
    setTimeout(() => router.push('/login'), 2000);
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Reset failed. The link may have expired.';
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
      <h1 class="auth-title">Set New Password</h1>
      <p class="auth-subtitle">Enter your new password below.</p>

      <CoarNote v-if="success" variant="success" padding="s" class="mb-4">
        Password updated! Redirecting to sign in…
      </CoarNote>

      <template v-else>
        <CoarNote v-if="error" variant="error" padding="s" class="mb-4">{{ error }}</CoarNote>

        <form v-if="email && token" @submit.prevent="onSubmit">
          <div class="form-group">
            <div class="readonly-field">
              <span class="readonly-label">Email</span>
              <span class="readonly-value">{{ email }}</span>
            </div>
          </div>
          <div class="form-group">
            <CoarPasswordInput v-model="newPassword" label="New Password" :required="true" autocomplete="new-password" />
          </div>
          <div class="form-group">
            <CoarPasswordInput v-model="confirmPassword" label="Confirm New Password" :required="true" autocomplete="new-password" />
          </div>
          <CoarButton type="submit" variant="primary" :full-width="true" :loading="isLoading">
            Reset Password
          </CoarButton>
        </form>

        <p v-else class="auth-footer">
          <RouterLink to="/forgot-password" class="link">Request a new reset link</RouterLink>
        </p>
      </template>
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
.readonly-field { display: flex; flex-direction: column; gap: 0.25rem; }
.readonly-label { font-size: 0.8125rem; font-weight: 500; color: var(--coar-text-neutral-secondary); }
.readonly-value { font-size: 0.9375rem; color: var(--coar-text-neutral-primary); }
.link { color: var(--coar-text-accent-primary); text-decoration: none; font-size: 0.875rem; }
.link:hover { text-decoration: underline; }
.auth-footer { margin: 1.5rem 0 0; text-align: center; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
</style>
