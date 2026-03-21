<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRoute } from 'vue-router';
import { CoarCard, CoarButton, CoarTextInput, CoarNote, CoarSpinner } from '@cocoar/vue-ui';
import { realmContext } from '@/composables/useRealmContext';

const route = useRoute();

const userCode = ref('');
const isLoading = ref(false);
const isSubmitted = ref(false);
const error = ref('');

onMounted(() => {
  // Pre-fill from query parameter if present (e.g., /device?user_code=ABCD-1234)
  const code = route.query.user_code as string;
  if (code) {
    userCode.value = code;
  }
});

async function onApprove() {
  if (!userCode.value) return;
  isLoading.value = true;
  error.value = '';
  try {
    // Submit approval to the OpenIddict verification endpoint
    const form = new URLSearchParams();
    form.set('user_code', userCode.value);

    const response = await fetch(`${realmContext.apiUrl.replace('/api', '')}/connect/verify`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: form.toString(),
    });

    if (response.ok || response.status === 302 || response.status === 200) {
      isSubmitted.value = true;
    } else {
      const text = await response.text();
      if (text.includes('invalid') || text.includes('expired')) {
        error.value = 'The code is invalid or has expired. Please try again on your device.';
      } else {
        error.value = 'Verification failed. Please check the code and try again.';
      }
    }
  } catch {
    error.value = 'An error occurred. Please try again.';
  } finally {
    isLoading.value = false;
  }
}

async function onDeny() {
  isLoading.value = true;
  error.value = '';
  try {
    const form = new URLSearchParams();
    form.set('user_code', userCode.value);
    form.set('deny', 'true');

    await fetch(`${realmContext.apiUrl.replace('/api', '')}/connect/verify`, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: form.toString(),
    });

    isSubmitted.value = true;
  } catch {
    error.value = 'An error occurred.';
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
        <template v-if="isSubmitted">
          <h1 class="auth-title">Device Authorized</h1>
          <p class="auth-subtitle">You can close this window and return to your device.</p>
          <CoarNote variant="info" padding="s">
            The device will automatically sign in within a few seconds.
          </CoarNote>
        </template>

        <template v-else>
          <h1 class="auth-title">Device Sign In</h1>
          <p class="auth-subtitle">Enter the code shown on your device to authorize it.</p>

          <CoarNote v-if="error" variant="error" padding="s" class="mb-4">
            {{ error }}
          </CoarNote>

          <form @submit.prevent="onApprove">
            <div class="form-group">
              <CoarTextInput
                v-model="userCode"
                label="Device Code"
                placeholder="XXXX-XXXX"
                :required="true"
                autocomplete="off"
              />
            </div>

            <div class="button-row">
              <CoarButton
                type="submit"
                variant="primary"
                :full-width="true"
                :loading="isLoading"
                :disabled="!userCode || isLoading"
              >
                Authorize Device
              </CoarButton>

              <CoarButton
                type="button"
                variant="ghost"
                :full-width="true"
                :disabled="!userCode || isLoading"
                @click="onDeny"
              >
                Deny
              </CoarButton>
            </div>
          </form>
        </template>
      </CoarCard>
    </div>
  </div>
</template>

<style scoped>
.auth-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
}

.auth-container {
  display: flex;
  flex-direction: column;
  align-items: center;
  width: 100%;
  max-width: 420px;
  gap: 1.25rem;
}

.auth-brand {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.auth-brand-icon { font-size: 1.5rem; line-height: 1; }
.auth-brand-name { font-size: 1.125rem; font-weight: 700; color: var(--coar-text-neutral-primary); }
.auth-card { width: 100%; }
.auth-title { margin: 0 0 0.375rem; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); }
.auth-subtitle { margin: 0 0 1.5rem; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
.form-group { margin-bottom: 1.25rem; }
.button-row { display: flex; flex-direction: column; gap: 0.5rem; }
.mb-4 { margin-bottom: 1rem; }
</style>
