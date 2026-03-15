<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRoute } from 'vue-router';
import { CoarCard, CoarNote, CoarButton, CoarSpinner } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';

const route = useRoute();
const status = ref<'loading' | 'success' | 'error'>('loading');
const errorMessage = ref('');

onMounted(async () => {
  const userId = route.query.userId as string;
  const token = route.query.token as string;

  if (!userId || !token) {
    status.value = 'error';
    errorMessage.value = 'Invalid confirmation link.';
    return;
  }

  try {
    await authApi.confirmEmail(userId, token);
    status.value = 'success';
  } catch {
    status.value = 'error';
    errorMessage.value = 'Confirmation failed. The link may have expired.';
  }
});
</script>

<template>
  <div class="auth-page">
    <CoarCard elevated padding="l" class="auth-card">
      <div v-if="status === 'loading'" class="centered">
        <CoarSpinner size="l" />
        <p>Confirming your email…</p>
      </div>

      <template v-else-if="status === 'success'">
        <h1 class="auth-title">Email Confirmed</h1>
        <CoarNote variant="success" padding="s" class="mb-4">
          Your email has been confirmed. You can now sign in.
        </CoarNote>
        <CoarButton variant="primary" :full-width="true" @click="$router.push('/login')">
          Sign In
        </CoarButton>
      </template>

      <template v-else>
        <h1 class="auth-title">Confirmation Failed</h1>
        <CoarNote variant="error" padding="s" class="mb-4">{{ errorMessage }}</CoarNote>
        <CoarButton variant="ghost" :full-width="true" @click="$router.push('/login')">
          Back to Sign In
        </CoarButton>
      </template>
    </CoarCard>
  </div>
</template>

<style scoped>
.auth-page { min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 2rem; }
.auth-card { width: 100%; max-width: 420px; }
.auth-title { margin: 0 0 1rem; font-size: 1.5rem; font-weight: 600; color: var(--coar-text-neutral-primary); }
.centered { display: flex; flex-direction: column; align-items: center; gap: 1rem; padding: 2rem 0; }
.mb-4 { margin-bottom: 1rem; }
</style>
