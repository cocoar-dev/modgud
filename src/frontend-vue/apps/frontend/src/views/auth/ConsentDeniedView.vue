<script setup lang="ts">
import { computed } from 'vue';
import { useRoute } from 'vue-router';
import { CoarCard, CoarButton, CoarNote } from '@cocoar/vue-ui';

const route = useRoute();

const errorDescription = computed(() => {
  return (route.query.error_description as string) || 'You denied the authorization request.';
});
</script>

<template>
  <div class="auth-page">
    <div class="auth-container">
      <div class="auth-brand">
        <span class="auth-brand-icon">&#9889;</span>
        <span class="auth-brand-name">Cocoar Auth</span>
      </div>

      <CoarCard elevated padding="l" class="auth-card">
        <h1 class="auth-title">Access Denied</h1>

        <CoarNote variant="warning" padding="s" class="mb-4">
          {{ errorDescription }}
        </CoarNote>

        <p class="auth-subtitle">
          You can close this window or return to the home page.
        </p>

        <CoarButton
          variant="secondary"
          :full-width="true"
          @click="$router.push('/')"
        >
          Go to Home
        </CoarButton>
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
.auth-card { width: 100%; background: #ffffff !important; }
:deep(.auth-card.coar-card--elevated) { box-shadow: 0 24px 64px rgba(0,0,0,0.35), 0 8px 24px rgba(0,0,0,0.2) !important; }
.auth-title { margin: 0 0 0.375rem; font-size: 1.375rem; font-weight: 700; color: var(--coar-text-neutral-primary); letter-spacing: -0.02em; }
.auth-subtitle { margin: 0 0 1.5rem; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
.mb-4 { margin-bottom: 1rem; }
</style>
