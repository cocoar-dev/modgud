<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarCard, CoarButton, CoarTextInput, CoarPasswordInput, CoarNote, CoarSpinner } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';

const router = useRouter();

const isChecking = ref(true);
const needsSetup = ref(false);

const userName = ref('');
const email = ref('');
const password = ref('');
const firstName = ref('');
const lastName = ref('');

const isLoading = ref(false);
const error = ref('');

onMounted(async () => {
  try {
    const status = await authApi.getSetupStatus();
    needsSetup.value = status.needsSetup;
    if (!status.needsSetup) {
      router.push('/login');
    }
  } catch {
    router.push('/login');
  } finally {
    isChecking.value = false;
  }
});

async function onSubmit() {
  if (!userName.value || !password.value) return;
  isLoading.value = true;
  error.value = '';
  try {
    await authApi.createAdmin({
      userName: userName.value,
      password: password.value,
      email: email.value || undefined,
      firstName: firstName.value || undefined,
      lastName: lastName.value || undefined,
    });
    router.push('/login');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Setup failed. Please try again.';
  } finally {
    isLoading.value = false;
  }
}
</script>

<template>
  <div class="auth-page">
    <div v-if="isChecking" class="centered">
      <CoarSpinner size="l" />
    </div>

    <CoarCard v-else-if="needsSetup" elevated padding="l" class="auth-card">
      <h1 class="auth-title">Initial Setup</h1>
      <p class="auth-subtitle">Create the first administrator account.</p>

      <CoarNote v-if="error" variant="error" padding="s" class="mb-4">{{ error }}</CoarNote>

      <form @submit.prevent="onSubmit">
        <div class="form-row-2">
          <CoarTextInput v-model="firstName" label="First Name" />
          <CoarTextInput v-model="lastName" label="Last Name" />
        </div>
        <div class="form-group">
          <CoarTextInput v-model="userName" label="Username" :required="true" autocomplete="username" />
        </div>
        <div class="form-group">
          <CoarTextInput v-model="email" label="Email" type="email" autocomplete="email" />
        </div>
        <div class="form-group">
          <CoarPasswordInput v-model="password" label="Password" :required="true" autocomplete="new-password" />
        </div>
        <CoarButton type="submit" variant="primary" :full-width="true" :loading="isLoading">
          Create Admin Account
        </CoarButton>
      </form>
    </CoarCard>
  </div>
</template>

<style scoped>
.auth-page { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem; }
.auth-card { width: 100%; max-width: 480px; }
.auth-title { margin: 0 0 0.5rem; font-size: 1.5rem; font-weight: 600; color: var(--coar-text-neutral-primary); }
.auth-subtitle { margin: 0 0 1.5rem; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
.form-group { margin-bottom: 1rem; }
.form-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; margin-bottom: 1rem; }
.mb-4 { margin-bottom: 1rem; }
.centered { display: flex; align-items: center; justify-content: center; min-height: 100vh; }
</style>
