<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import {
  CoarCard, CoarNote, CoarTextInput, CoarCheckbox, CoarSpinner, CoarSwitch, useToast,
} from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import { parseLines } from '@/core/utils/text';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();

const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);

const name = ref('');
const displayName = ref('');
const description = ref('');
const resources = ref('');
const enabled = ref(true);
const required = ref(false);
const emphasize = ref(false);
const showInDiscoveryDocument = ref(true);
const userClaims = ref('');

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

watch([name, displayName, description, resources, enabled, required, emphasize, showInDiscoveryDocument, userClaims], () => { isDirty.value = true; });

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit Scope' : 'Create Scope';
  ctx.header.subTitle = isEditMode.value ? 'Update scope details' : 'Create a new OAuth scope';
  ctx.content.scrollable = true;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/oauth/scopes');
  ctx.footer.button2.visible = isEditMode.value;
  ctx.footer.button2.text = 'Delete';
  ctx.footer.button2.onClick = () => onDelete();
  ctx.footer.button3.visible = true;
  ctx.footer.button3.text = isEditMode.value ? 'Save Changes' : 'Create';
  ctx.footer.button3.onClick = () => onSubmit();
});

watch(isSaving, (val) => { ui.state.footer.button3.loading = val; });

const toast = useToast();

async function onDelete() {
  if (!confirm('Are you sure you want to delete this scope?')) return;
  try {
    await adminApi.deleteOAuthScope(id.value!);
    isDirty.value = false;
    toast.success('Scope deleted.');
    router.push('/admin/oauth/scopes');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to delete scope.';
  }
}

onMounted(async () => {
  if (!isEditMode.value) return;
  isLoading.value = true;
  try {
    const scope = await adminApi.getOAuthScope(id.value!);
    name.value = scope.name;
    displayName.value = scope.displayName || '';
    description.value = scope.description || '';
    resources.value = scope.resources.join('\n');
    enabled.value = scope.enabled;
    required.value = scope.required;
    emphasize.value = scope.emphasize;
    showInDiscoveryDocument.value = scope.showInDiscoveryDocument;
    userClaims.value = (scope.userClaims || []).join('\n');
  } catch {
    error.value = 'Failed to load scope.';
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
      await adminApi.updateOAuthScope(id.value!, {
        displayName: displayName.value || undefined,
        description: description.value || undefined,
        resources: parseLines(resources.value),
        enabled: enabled.value,
        required: required.value,
        emphasize: emphasize.value,
        showInDiscoveryDocument: showInDiscoveryDocument.value,
        userClaims: parseLines(userClaims.value),
      });
    } else {
      await adminApi.createOAuthScope({
        name: name.value,
        displayName: displayName.value || undefined,
        description: description.value || undefined,
        resources: parseLines(resources.value),
        enabled: enabled.value,
        required: required.value,
        emphasize: emphasize.value,
        showInDiscoveryDocument: showInDiscoveryDocument.value,
        userClaims: parseLines(userClaims.value),
      });
    }
    isDirty.value = false;
    router.push('/admin/oauth/scopes');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save scope.';
  } finally {
    isSaving.value = false;
  }
}


</script>

<template>
  <div class="form-page">
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>

    <template v-else>
      <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>

      <form @submit.prevent="onSubmit">
        <div class="form-layout">
          <!-- Left column: Main form fields -->
          <div class="form-main">
            <CoarCard padding="l" class="form-card">
              <h2 class="section-title">Details</h2>
              <div class="form-group">
                <CoarTextInput v-model="name" label="Name" :required="true" :disabled="isEditMode" />
              </div>
              <div class="form-group">
                <CoarTextInput v-model="displayName" label="Display Name" />
              </div>
              <div class="form-group">
                <CoarTextInput v-model="description" label="Description" :rows="3" />
              </div>
              <div class="form-group">
                <CoarTextInput v-model="resources" label="API Resources (one per line)" :rows="3" />
              </div>
            </CoarCard>
          </div>

          <!-- Right sidebar: Options -->
          <div class="form-sidebar">
            <CoarCard padding="l" class="form-card">
              <h2 class="section-title">Options</h2>
              <div class="form-group">
                <CoarSwitch v-model="enabled" label="Enabled" />
              </div>
              <div class="form-group">
                <CoarCheckbox v-model="required" label="Required" />
              </div>
              <div class="form-group">
                <CoarCheckbox v-model="emphasize" label="Emphasize" />
              </div>
              <div class="form-group">
                <CoarCheckbox v-model="showInDiscoveryDocument" label="Show In Discovery Document" />
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

.form-group { margin-bottom: 1rem; }
.form-group:last-child { margin-bottom: 0; }
.form-actions { display: flex; gap: 0.75rem; }
.mb-3 { margin-bottom: 0.75rem; }
.mt-3 { margin-top: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
</style>
