<script setup lang="ts">
import { ref } from 'vue';
import { CoarCard, CoarButton, CoarTextInput, CoarNote } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';

const email = ref('');
const isLoading = ref(false);
const error = ref('');
const success = ref(false);

async function onSubmit() {
  if (!email.value) return;
  isLoading.value = true;
  error.value = '';
  try {
    await authApi.forgotPassword({ email: email.value });
    success.value = true;
  } catch {
    // Always show the ambiguous success message to prevent enumeration
    success.value = true;
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
        <h1 class="auth-title">Reset Password</h1>
        <p class="auth-subtitle">Enter your email and we'll send you a reset link.</p>

        <CoarNote v-if="success" variant="success" padding="s" class="mb-4">
          If an account with that email exists, a reset link has been sent.
        </CoarNote>

        <template v-else>
          <CoarNote v-if="error" variant="error" padding="s" class="mb-4">{{ error }}</CoarNote>

          <form @submit.prevent="onSubmit">
            <div class="form-group">
              <CoarTextInput v-model="email" label="Email" type="email" :required="true" autocomplete="email" />
            </div>
            <CoarButton type="submit" variant="primary" :full-width="true" :loading="isLoading">
              Send Reset Link
            </CoarButton>
          </form>
        </template>

        <p class="auth-footer">
          <RouterLink to="/login" class="link">Back to sign in</RouterLink>
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
</style>
