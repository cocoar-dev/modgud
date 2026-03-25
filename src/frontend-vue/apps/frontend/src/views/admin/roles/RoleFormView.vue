<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { CoarCard, CoarNote, CoarTextInput, CoarSelect, CoarSpinner, CoarTabGroup, CoarTab, useToast } from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import type { OAuthApi } from '@/core/models/oauth.models';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();

const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);

// Tabs
const activeTab = ref<string>('basic');

// Form fields
const name = ref('');
const displayName = ref('');
const emailField = ref('');
const description = ref('');
const boundToApi = ref('');

// APIs for dropdown
const apis = ref<OAuthApi[]>([]);

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

const apiOptions = computed(() => [
  { value: '', label: '(None)' },
  ...apis.value.map((r) => ({ value: r.id, label: r.displayName || r.name })),
]);

watch([name, displayName, emailField, description, boundToApi], () => { isDirty.value = true; });

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit Role' : 'Create Role';
  ctx.header.subTitle = isEditMode.value ? 'Update role details' : 'Create a new role';
  ctx.content.scrollable = true;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/roles');
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
  if (!confirm('Are you sure you want to delete this role?')) return;
  try {
    await adminApi.deleteRole(id.value!);
    isDirty.value = false;
    toast.success('Role deleted.');
    router.push('/admin/roles');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to delete role.';
  }
}

onMounted(async () => {
  isLoading.value = true;
  try {
    const [roleData, resourcesResult] = await Promise.all([
      isEditMode.value ? adminApi.getRole(id.value!) : Promise.resolve(null),
      adminApi.getOAuthApis(),
    ]);

    apis.value = resourcesResult.items;

    if (roleData) {
      name.value = roleData.name;
      description.value = roleData.description || '';
      // TODO: load displayName, email, boundToApi when backend supports it
    }
  } catch {
    error.value = 'Failed to load data.';
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
      await adminApi.updateRole(id.value!, {
        name: name.value,
        description: description.value || null,
        // TODO: send displayName, email, boundToApi when backend supports them
      });
    } else {
      await adminApi.createRole({
        name: name.value,
        description: description.value || undefined,
        // TODO: send displayName, email, boundToApi when backend supports them
      });
    }
    isDirty.value = false;
    router.push('/admin/roles');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save role.';
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
        <CoarTabGroup v-model="activeTab">
          <CoarTab id="basic">
            <template #default>Basic Info</template>
            <template #content>
              <div class="form-layout">
                <!-- Left column: Main form fields -->
                <div class="form-main">
                  <CoarCard padding="l" class="form-card">
                    <div class="form-group">
                      <CoarTextInput v-model="name" label="Name" :required="true" />
                    </div>
                    <div class="form-group">
                      <CoarTextInput v-model="displayName" label="Display Name" />
                    </div>
                    <div class="form-group">
                      <CoarTextInput v-model="emailField" label="Email" />
                    </div>
                    <div class="form-group">
                      <CoarTextInput v-model="description" label="Description" :rows="3" />
                    </div>
                  </CoarCard>
                </div>

                <!-- Right sidebar: Bound API -->
                <div class="form-sidebar">
                  <CoarCard padding="l" class="form-card">
                    <h2 class="section-title">Bound To API</h2>
                    <div class="form-group">
                      <CoarSelect
                        v-model="boundToApi"
                        label="API"
                        :options="apiOptions"
                      />
                    </div>
                  </CoarCard>
                </div>
              </div>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="members">
            <template #default>Members</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <h2 class="section-title">Members</h2>
                <p class="placeholder-text">
                  Members list will be loaded here once the API endpoint is available.
                </p>
              </CoarCard>
            </template>
          </CoarTab>
        </CoarTabGroup>
      </form>
    </template>
  </div>
</template>

<style scoped>
.form-page { }

/* 2-column layout */
.form-layout { display: grid; grid-template-columns: 1fr 300px; gap: 1.5rem; align-items: start; }
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

.placeholder-text {
  font-size: 0.875rem;
  color: var(--coar-text-neutral-secondary);
  padding: 1.5rem 0;
  text-align: center;
}
</style>
