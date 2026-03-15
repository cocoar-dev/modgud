<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { CoarCard, CoarButton, CoarNote, CoarSpinner, CoarPasswordInput, useToast } from '@cocoar/vue-ui';
import { authApi } from '@/core/api/auth-api';
import { ApiError } from '@/core/api/http';
import { useUI } from '@/composables/useUI';
import type { DeletionStatus } from '@/core/models/auth.models';

const ui = useUI();

const toast = useToast();
const deletionStatus = ref<DeletionStatus | null>(null);
const isLoading = ref(true);
const error = ref('');
const isExporting = ref(false);

// Deletion form state
const showDeletionForm = ref(false);
const deletionPassword = ref('');
const isDeletionLoading = ref(false);

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = 'Privacy & Data';
  ctx.content.scrollable = true;
});

onMounted(async () => {
  try {
    deletionStatus.value = await authApi.getDeletionStatus();
  } catch {
    // Non-critical
  } finally {
    isLoading.value = false;
  }
});

async function exportData() {
  error.value = '';
  isExporting.value = true;
  try {
    const data = await authApi.exportData();
    if (data) {
      const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'my-data-export.json';
      a.click();
      URL.revokeObjectURL(url);
      toast.success('Your data export has been downloaded.');
    }
  } catch (err) {
    toast.error(err instanceof ApiError ? err.message : 'Export failed.');
  } finally {
    isExporting.value = false;
  }
}

async function requestDeletion() {
  if (!deletionPassword.value) return;
  isDeletionLoading.value = true;
  error.value = '';
  try {
    await authApi.requestDeletion({ password: deletionPassword.value });
    deletionStatus.value = await authApi.getDeletionStatus();
    showDeletionForm.value = false;
    deletionPassword.value = '';
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Request failed.';
  } finally {
    isDeletionLoading.value = false;
  }
}

async function cancelDeletion() {
  error.value = '';
  try {
    await authApi.cancelDeletion();
    deletionStatus.value = await authApi.getDeletionStatus();
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Cancel failed.';
  }
}

function formatDate(dateStr: string) {
  return new Date(dateStr).toLocaleString();
}
</script>

<template>
  <div class="page">
    <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>

    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>

    <template v-else>
      <CoarCard padding="l" class="section-card">
        <h2 class="section-title">Export Your Data</h2>
        <p class="section-desc">Download a copy of all your personal data (GDPR Article 20).</p>
        <CoarButton variant="primary" :loading="isExporting" @click="exportData">Download My Data</CoarButton>
      </CoarCard>

      <CoarCard padding="l" variant="error" class="section-card">
        <h2 class="section-title">Delete Account</h2>

        <template v-if="deletionStatus?.isPending">
          <CoarNote variant="warning" padding="s" class="mb-3">
            Account deletion is pending. Check your email for the confirmation link.
          </CoarNote>
          <div v-if="deletionStatus.requestedAt || deletionStatus.confirmationDeadline" class="deletion-meta mb-3">
            <p v-if="deletionStatus.requestedAt" class="meta-line">
              Requested: {{ formatDate(deletionStatus.requestedAt) }}
            </p>
            <p v-if="deletionStatus.confirmationDeadline" class="meta-line">
              Confirmation required by: {{ formatDate(deletionStatus.confirmationDeadline) }}
            </p>
          </div>
          <CoarButton variant="ghost" @click="cancelDeletion">Cancel Deletion Request</CoarButton>
        </template>

        <template v-else-if="showDeletionForm">
          <CoarNote variant="error" padding="s" class="mb-3">
            This action is irreversible. Your account and all associated data will be permanently deleted.
          </CoarNote>
          <div class="form-group">
            <CoarPasswordInput
              v-model="deletionPassword"
              label="Enter your password to confirm"
              autocomplete="current-password"
            />
          </div>
          <div class="form-actions">
            <CoarButton variant="danger" :loading="isDeletionLoading" @click="requestDeletion">
              Confirm Deletion Request
            </CoarButton>
            <CoarButton variant="ghost" @click="showDeletionForm = false; deletionPassword = ''">
              Cancel
            </CoarButton>
          </div>
        </template>

        <template v-else>
          <p class="section-desc">
            Permanently delete your account and all associated data. This action cannot be undone.
          </p>
          <CoarButton variant="danger" @click="showDeletionForm = true">Request Account Deletion</CoarButton>
        </template>
      </CoarCard>
    </template>
  </div>
</template>

<style scoped>
.page { }
.section-card { margin-bottom: 1.25rem; }
.section-title { margin: 0; }  /* global .section-title handles divider + spacing */
.section-desc { margin: 0 0 1rem; color: var(--coar-text-neutral-secondary); font-size: 0.875rem; line-height: 1.5; }
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
.form-group { margin-bottom: 1rem; }
.form-actions { display: flex; gap: 0.75rem; }
.deletion-meta { display: flex; flex-direction: column; gap: 0.25rem; }
.meta-line { margin: 0; font-size: 0.875rem; color: var(--coar-text-neutral-secondary); }
</style>
