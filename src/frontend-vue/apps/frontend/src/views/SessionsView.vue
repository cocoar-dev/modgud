<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { CoarCard, CoarButton, CoarNote, CoarSpinner, CoarTag, CoarIcon, CoarPopconfirm, useToast } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';
import { useUI } from '@/composables/useUI';
import type { Session } from '@/core/models/auth.models';

const ui = useUI();

const toast = useToast();
const sessions = ref<Session[]>([]);
const isLoading = ref(true);
const error = ref('');

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = 'Active Sessions';
  ctx.content.scrollable = true;
});

onMounted(() => {
  loadSessions();
});

async function loadSessions() {
  isLoading.value = true;
  error.value = '';
  try {
    const result = await authApi.getSessions();
    sessions.value = result.sessions;
  } catch {
    error.value = 'Failed to load sessions.';
  } finally {
    isLoading.value = false;
  }
}

async function revokeSession(id: string) {
  error.value = '';
  try {
    await authApi.revokeSession(id);
    sessions.value = sessions.value.filter((s) => s.id !== id);
    toast.success('Session revoked successfully.');
  } catch (err) {
    toast.error(err instanceof ApiError ? err.message : 'Failed to revoke session.');
  }
}

async function revokeAll() {
  error.value = '';
  try {
    await authApi.revokeAllSessions();
    toast.success('All other sessions have been revoked.');
    await loadSessions();
  } catch (err) {
    toast.error(err instanceof ApiError ? err.message : 'Failed to revoke sessions.');
  }
}

function formatDate(dateStr: string) {
  return new Date(dateStr).toLocaleString();
}
</script>

<template>
  <div class="page">
    <div class="page-actions">
      <CoarPopconfirm
        message="This will sign you out of all other devices. Continue?"
        confirm-text="Revoke All"
        confirm-variant="danger"
        @confirmed="revokeAll"
      >
        <CoarButton variant="danger" size="s">Revoke All Others</CoarButton>
      </CoarPopconfirm>
    </div>

    <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>

    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>

    <div v-else class="sessions-list">
      <CoarCard v-for="session in sessions" :key="session.id" padding="m" class="session-card section-card">
        <div class="session-icon-wrap" :class="session.isCurrent ? 'session-icon-wrap--current' : ''">
          <CoarIcon name="key" class="session-icon" />
        </div>
        <div class="session-info">
          <div class="session-device">
            <span class="session-browser">{{ session.browser || 'Unknown Browser' }}</span>
            <span v-if="session.browserVersion" class="session-version"> {{ session.browserVersion }}</span>
            <CoarTag v-if="session.isCurrent" variant="success" size="s" class="ml-2">Current</CoarTag>
          </div>
          <div class="session-meta">
            <span>{{ session.operatingSystem || 'Unknown OS' }}</span>
            <span v-if="session.ipAddress"> · {{ session.ipAddress }}</span>
            <span> · Started {{ formatDate(session.createdAt) }}</span>
            <span> · Active {{ formatDate(session.lastActiveAt) }}</span>
          </div>
        </div>
        <CoarPopconfirm
          v-if="!session.isCurrent"
          message="Revoke this session? The device will be signed out immediately."
          confirm-text="Revoke"
          confirm-variant="danger"
          @confirmed="revokeSession(session.id)"
        >
          <CoarButton variant="ghost" size="s">Revoke</CoarButton>
        </CoarPopconfirm>
      </CoarCard>

      <p v-if="sessions.length === 0" class="empty-state">No active sessions found.</p>
    </div>
  </div>
</template>

<style scoped>
.page { }
.page-actions { display: flex; justify-content: flex-end; margin-bottom: 1.5rem; }
.sessions-list { display: flex; flex-direction: column; gap: 0.75rem; }
.session-card { display: flex; align-items: center; gap: 1rem; }
.session-icon-wrap {
  flex-shrink: 0;
  width: 2.5rem;
  height: 2.5rem;
  border-radius: 8px;
  background: var(--coar-color-slate-100);
  display: flex;
  align-items: center;
  justify-content: center;
}
.session-icon-wrap--current { background: var(--coar-color-accent-50, #eff6ff); }
.session-icon { width: 1.125rem; height: 1.125rem; color: var(--coar-color-slate-500); }
.session-icon-wrap--current .session-icon { color: var(--coar-color-accent-600, #2563eb); }
.session-info { flex: 1; min-width: 0; }
.session-device { font-weight: 500; font-size: 0.9375rem; margin-bottom: 0.2rem; display: flex; align-items: center; flex-wrap: wrap; gap: 0.25rem; }
.session-version { color: var(--coar-text-neutral-secondary); font-weight: 400; }
.session-meta { font-size: 0.8125rem; color: var(--coar-text-neutral-secondary); }
.empty-state { color: var(--coar-text-neutral-secondary); text-align: center; padding: 2rem; }
</style>
