<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import {
  CoarCard, CoarButton, CoarNote, CoarTextInput, CoarCheckbox, CoarSpinner, CoarCodeBlock, CoarSwitch, useToast,
} from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import { parseLines } from '@/core/utils/text';
import type { ApiSecretEntry } from '@/core/models/oauth.models';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();

const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);

const name = ref('');
const displayName = ref('');
const description = ref('');
const enabled = ref(true);
const scopes = ref('');
const userClaims = ref('');

const secrets = ref<ApiSecretEntry[]>([]);
const newSecret = ref('');
const newSecretDescription = ref('');
const newSecretExpiration = ref('');
const isAddingSecret = ref(false);
const isCreatingSecret = ref(false);

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

watch([name, displayName, description, enabled, scopes, userClaims], () => { isDirty.value = true; });

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit API' : 'Create API';
  ctx.header.subTitle = isEditMode.value ? 'Update API configuration' : 'Register a new API';
  ctx.content.scrollable = true;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/oauth/apis');
  ctx.footer.button2.visible = isEditMode.value;
  ctx.footer.button2.text = 'Delete';
  ctx.footer.button2.onClick = () => onDeleteApi();
  ctx.footer.button3.visible = true;
  ctx.footer.button3.text = isEditMode.value ? 'Save Changes' : 'Create';
  ctx.footer.button3.onClick = () => onSubmit();
});

watch(isSaving, (val) => { ui.state.footer.button3.loading = val; });

const toast = useToast();

async function onDeleteApi() {
  if (!confirm('Are you sure you want to delete this API?')) return;
  try {
    await adminApi.deleteOAuthApi(id.value!);
    isDirty.value = false;
    toast.success('API deleted.');
    router.push('/admin/oauth/apis');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to delete API.';
  }
}

onMounted(async () => {
  if (!isEditMode.value) return;
  isLoading.value = true;
  try {
    const resource = await adminApi.getOAuthApi(id.value!);
    name.value = resource.name;
    displayName.value = resource.displayName || '';
    description.value = resource.description || '';
    enabled.value = resource.enabled;
    scopes.value = resource.scopes.join('\n');
    userClaims.value = resource.userClaims.join('\n');
    secrets.value = resource.secrets || [];
  } catch {
    error.value = 'Failed to load API.';
  } finally {
    isLoading.value = false;
    setTimeout(() => { isDirty.value = false; }, 0);
  }
});

async function onSubmit() {
  if (!name.value) return;
  isSaving.value = true;
  error.value = '';
  try {
    if (isEditMode.value) {
      await adminApi.updateOAuthApi(id.value!, {
        displayName: displayName.value || undefined,
        description: description.value || undefined,
        enabled: enabled.value,
        scopes: parseLines(scopes.value),
        userClaims: parseLines(userClaims.value),
      });
      isDirty.value = false;
      router.push('/admin/oauth/apis');
    } else {
      const result = await adminApi.createOAuthApi({
        name: name.value,
        displayName: displayName.value || undefined,
        description: description.value || undefined,
        enabled: enabled.value,
        scopes: parseLines(scopes.value),
        userClaims: parseLines(userClaims.value),
      });
      isDirty.value = false;
      if (result.apiSecret) {
        newSecret.value = result.apiSecret;
      } else {
        router.push('/admin/oauth/apis');
      }
    }
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save API.';
  } finally {
    isSaving.value = false;
  }
}

async function onCreateSecret() {
  if (!id.value) return;
  isCreatingSecret.value = true;
  error.value = '';
  try {
    const result = await adminApi.createApiSecret(id.value, {
      description: newSecretDescription.value || undefined,
      expiration: newSecretExpiration.value || undefined,
    });
    newSecret.value = result.apiSecret;
    // Reload to get updated secrets list
    const resource = await adminApi.getOAuthApi(id.value);
    secrets.value = resource.secrets || [];
    isAddingSecret.value = false;
    newSecretDescription.value = '';
    newSecretExpiration.value = '';
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to create secret.';
  } finally {
    isCreatingSecret.value = false;
  }
}

async function onDeleteSecret(secretId: string) {
  if (!id.value || !confirm('Delete this secret? This cannot be undone.')) return;
  error.value = '';
  try {
    await adminApi.deleteApiSecret(id.value, secretId);
    secrets.value = secrets.value.filter(s => s.secretId !== secretId);
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to delete secret.';
  }
}

function formatDate(dateStr?: string): string {
  if (!dateStr) return '--';
  return new Date(dateStr).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function maskValue(): string {
  return '********';
}
</script>

<template>
  <div class="form-page">
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>

    <template v-else-if="newSecret">
      <CoarNote variant="warning" padding="s" class="mb-3">
        Save this API secret now -- it will not be shown again.
      </CoarNote>
      <CoarCard padding="l" class="form-card">
        <CoarCodeBlock :code="newSecret" language="text" />
        <CoarButton variant="primary" class="mt-3" @click="newSecret = ''; if (!isEditMode) router.push('/admin/oauth/apis');">Done</CoarButton>
      </CoarCard>
    </template>

    <template v-else>
      <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>

      <form @submit.prevent="onSubmit">
        <div class="form-layout">
          <!-- Left column: Main form fields -->
          <div class="form-main">
            <CoarCard padding="l" class="form-card">
              <h2 class="section-title">Details</h2>
              <div class="form-group">
                <CoarTextInput v-model="name" label="API Name" :required="true" :disabled="isEditMode" />
              </div>
              <div class="form-group">
                <CoarTextInput v-model="displayName" label="Display Name" />
              </div>
              <div class="form-group">
                <CoarTextInput v-model="description" label="Description" :rows="3" />
              </div>
              <div class="form-group">
                <CoarTextInput v-model="scopes" label="Scopes (one per line)" :rows="3" />
              </div>
            </CoarCard>

            <!-- Secrets section (only in edit mode) -->
            <CoarCard v-if="isEditMode" padding="l" class="form-card mt-3">
              <div class="section-header">
                <h2 class="section-title">API Secrets</h2>
                <CoarButton v-if="!isAddingSecret" type="button" variant="ghost" size="s" @click="isAddingSecret = true">Add Secret</CoarButton>
              </div>

              <!-- Add secret form -->
              <div v-if="isAddingSecret" class="add-secret-form">
                <div class="form-row-2">
                  <CoarTextInput v-model="newSecretDescription" label="Description (optional)" />
                  <CoarTextInput v-model="newSecretExpiration" label="Expiration (optional, e.g. 2025-12-31)" />
                </div>
                <div class="form-actions-inline">
                  <CoarButton type="button" variant="primary" size="s" :loading="isCreatingSecret" @click="onCreateSecret">Create Secret</CoarButton>
                  <CoarButton type="button" variant="ghost" size="s" @click="isAddingSecret = false; newSecretDescription = ''; newSecretExpiration = '';">Cancel</CoarButton>
                </div>
              </div>

              <!-- Secrets table -->
              <div v-if="secrets.length > 0" class="secrets-table-wrapper">
                <table class="secrets-table">
                  <thead>
                    <tr>
                      <th>Type</th>
                      <th>Value</th>
                      <th>Description</th>
                      <th>Expiration</th>
                      <th>Created</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr v-for="secret in secrets" :key="secret.secretId">
                      <td>{{ secret.type }}</td>
                      <td class="mono">{{ maskValue() }}</td>
                      <td>{{ secret.description || '--' }}</td>
                      <td>{{ formatDate(secret.expiration) }}</td>
                      <td>{{ formatDate(secret.createdAt) }}</td>
                      <td>
                        <CoarButton type="button" variant="ghost" size="s" @click="onDeleteSecret(secret.secretId)">Delete</CoarButton>
                      </td>
                    </tr>
                  </tbody>
                </table>
              </div>
              <p v-else class="empty-text">No secrets configured.</p>
            </CoarCard>
          </div>

          <!-- Right sidebar: Options -->
          <div class="form-sidebar">
            <CoarCard padding="l" class="form-card">
              <h2 class="section-title">Options</h2>
              <div class="form-group">
                <CoarSwitch v-model="enabled" label="Enabled" />
              </div>
            </CoarCard>

            <CoarCard padding="l" class="form-card mt-3">
              <h2 class="section-title">User Claims</h2>
              <div class="form-group">
                <CoarTextInput v-model="userClaims" label="Claims (one per line)" :rows="6" />
              </div>
            </CoarCard>
          </div>
        </div>

      </form>
    </template>
  </div>
</template>

<style scoped>
.form-page { }

.form-layout { display: grid; grid-template-columns: 1fr 320px; gap: 1.5rem; align-items: start; }
@media (max-width: 860px) {
  .form-layout { grid-template-columns: 1fr; }
}
.form-main { min-width: 0; }
.form-sidebar { min-width: 0; }

.section-title { margin: 0 0 1rem; font-size: 1rem; font-weight: 600; }
.section-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 1rem; }
.section-header .section-title { margin-bottom: 0; }

.form-group { margin-bottom: 1rem; }
.form-group:last-child { margin-bottom: 0; }
.form-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; margin-bottom: 0.75rem; }
.form-actions { display: flex; gap: 0.75rem; }
.form-actions-inline { display: flex; gap: 0.5rem; margin-top: 0.25rem; }
.mb-3 { margin-bottom: 0.75rem; }
.mt-3 { margin-top: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }

.add-secret-form { padding: 0.75rem; background: var(--coar-bg-neutral-subtle, #f9fafb); border-radius: 0.5rem; margin-bottom: 1rem; }

.secrets-table-wrapper { overflow-x: auto; }
.secrets-table { width: 100%; border-collapse: collapse; font-size: 0.8125rem; }
.secrets-table th { text-align: left; padding: 0.5rem 0.75rem; border-bottom: 1px solid var(--coar-border-neutral, #e5e7eb); font-weight: 600; color: var(--coar-text-neutral-secondary); white-space: nowrap; }
.secrets-table td { padding: 0.5rem 0.75rem; border-bottom: 1px solid var(--coar-border-neutral-subtle, #f3f4f6); }
.secrets-table .mono { font-family: monospace; letter-spacing: 0.05em; color: var(--coar-text-neutral-secondary); }
.empty-text { font-size: 0.8125rem; color: var(--coar-text-neutral-secondary); margin: 0; }
</style>
