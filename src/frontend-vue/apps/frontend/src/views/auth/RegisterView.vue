<script setup lang="ts">
import { ref, computed } from 'vue';
import { useRouter } from 'vue-router';
import { CoarCard, CoarButton, CoarTextInput, CoarPasswordInput, CoarNote } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';

const router = useRouter();

const userName = ref('');
const email = ref('');
const password = ref('');
const confirmPassword = ref('');
const firstName = ref('');
const lastName = ref('');

const touched = ref({ userName: false, email: false, password: false, confirmPassword: false });

const isLoading = ref(false);
const error = ref('');
const success = ref(false);

const userNameError = computed(() => {
  if (!touched.value.userName) return undefined;
  if (!userName.value) return 'Username is required';
  return undefined;
});

const emailError = computed(() => {
  if (!touched.value.email) return undefined;
  if (!email.value) return 'Email is required';
  if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.value)) return 'Enter a valid email address';
  return undefined;
});

const passwordError = computed(() => {
  if (!touched.value.password) return undefined;
  if (!password.value) return 'Password is required';
  return undefined;
});

const confirmPasswordError = computed(() => {
  if (!touched.value.confirmPassword) return undefined;
  if (!confirmPassword.value) return 'Please confirm your password';
  if (confirmPassword.value !== password.value) return 'Passwords do not match';
  return undefined;
});

const isValid = computed(
  () =>
    !!userName.value &&
    !!email.value &&
    /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.value) &&
    !!password.value &&
    confirmPassword.value === password.value,
);

async function onSubmit() {
  touched.value = { userName: true, email: true, password: true, confirmPassword: true };
  if (!isValid.value) return;

  isLoading.value = true;
  error.value = '';
  try {
    const result = await authApi.register({
      userName: userName.value,
      email: email.value,
      password: password.value,
      firstName: firstName.value || undefined,
      lastName: lastName.value || undefined,
    });

    if (result.succeeded) {
      if (result.requiresEmailConfirmation) {
        success.value = true;
      } else {
        router.push('/login');
      }
    } else {
      error.value = result.errors?.join(', ') || 'Registration failed.';
    }
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Registration failed.';
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
      <h1 class="auth-title">Create Account</h1>
      <p class="auth-subtitle">Fill in your details to get started.</p>

      <CoarNote v-if="success" variant="success" padding="s" class="mb-4">
        Registration successful! Please check your email to confirm your account.
      </CoarNote>

      <template v-else>
        <CoarNote v-if="error" variant="error" padding="s" class="mb-4">
          {{ error }}
        </CoarNote>

        <form @submit.prevent="onSubmit">
          <div class="form-row-2">
            <CoarTextInput v-model="firstName" label="First Name" placeholder="John" />
            <CoarTextInput v-model="lastName" label="Last Name" placeholder="Doe" />
          </div>
          <div class="form-group">
            <CoarTextInput
              v-model="userName"
              label="Username"
              placeholder="johndoe"
              :required="true"
              autocomplete="username"
              :error="userNameError"
              @blur="touched.userName = true"
            />
          </div>
          <div class="form-group">
            <CoarTextInput
              v-model="email"
              label="Email"
              placeholder="john@example.com"
              :required="true"
              type="email"
              autocomplete="email"
              :error="emailError"
              @blur="touched.email = true"
            />
          </div>
          <div class="form-group">
            <CoarPasswordInput
              v-model="password"
              label="Password"
              :required="true"
              autocomplete="new-password"
              :error="passwordError"
              @blur="touched.password = true"
            />
          </div>
          <div class="form-group">
            <CoarPasswordInput
              v-model="confirmPassword"
              label="Confirm Password"
              :required="true"
              autocomplete="new-password"
              :error="confirmPasswordError"
              @blur="touched.confirmPassword = true"
            />
          </div>

          <CoarButton type="submit" variant="primary" :full-width="true" :loading="isLoading">
            Create Account
          </CoarButton>
        </form>

        <p class="auth-footer">
          Already have an account?
          <RouterLink to="/login" class="link">Sign in</RouterLink>
        </p>
      </template>
    </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.auth-page { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem; }
.auth-container { display: flex; flex-direction: column; align-items: center; width: 100%; max-width: 480px; gap: 1.25rem; }
.auth-brand { display: flex; align-items: center; gap: 0.5rem; }
.auth-brand-icon { font-size: 1.5rem; line-height: 1; }
.auth-brand-name { font-size: 1.125rem; font-weight: 700; color: rgba(255,255,255,0.92); letter-spacing: -0.01em; }
.auth-card { width: 100%; }
.auth-title { margin: 0 0 0.375rem; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); letter-spacing: -0.02em; }
.auth-subtitle { margin: 0 0 1.5rem; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
.form-group { margin-bottom: 1rem; }
.form-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; margin-bottom: 1rem; }
.mb-4 { margin-bottom: 1rem; }
.link { color: var(--coar-text-accent-primary); text-decoration: none; font-size: 0.875rem; }
.link:hover { text-decoration: underline; }
.auth-footer { margin: 1.5rem 0 0; text-align: center; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
</style>
