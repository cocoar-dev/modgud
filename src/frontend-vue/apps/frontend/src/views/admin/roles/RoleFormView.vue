<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { CoarCard, CoarNote, CoarTextInput, CoarSelect, CoarSpinner, CoarTabGroup, CoarTab, useToast } from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import type { OAuthClient } from '@/core/models/oauth.models';

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
const clientId = ref('');

// OAuth clients for dropdown (realm role vs client role)
const clients = ref<OAuthClient[]>([]);

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

const clientOptions = computed(() => [
  { value: '', label: '(Realm Role — global)' },
  ...clients.value.map((c) => ({ value: c.id, label: c.displayName || c.clientId })),
]);

watch([name, displayName, emailField, description, clientId], () => { isDirty.value = true; });

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
    const [roleData, clientsResult] = await Promise.all([
      isEditMode.value ? adminApi.getRole(id.value!) : Promise.resolve(null),
      adminApi.getOAuthClients(),
    ]);

    clients.value = clientsResult.items;

    if (roleData) {
      name.value = roleData.name;
      displayName.value = roleData.displayName || '';
      emailField.value = roleData.email || '';
      description.value = roleData.description || '';
      clientId.value = roleData.clientId?.toString() || '';
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
        displayName: displayName.value || null,
        email: emailField.value || null,
        clientId: clientId.value || null,
      });
    } else {
      await adminApi.createRole({
        name: name.value,
        description: description.value || undefined,
        displayName: displayName.value || undefined,
        email: emailField.value || undefined,
        clientId: clientId.value || undefined,
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

                <!-- Right sidebar: Role Type (Realm vs Client) -->
                <div class="form-sidebar">
                  <CoarCard padding="l" class="form-card">
                    <h2 class="section-title">Role Type</h2>
                    <div class="form-group">
                      <CoarSelect
                        v-model="clientId"
                        label="Client"
                        :options="clientOptions"
                      />
                    </div>
                    <p class="hint-text">
                      Realm roles are global. Client roles are scoped to a specific OAuth client and appear in the token under <code>resource_access</code>.
                    </p>
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
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }

.hint-text {
  margin-top: 0.75rem;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary);
  line-height: 1.5;
}

.hint-text code {
  font-size: 0.75rem;
  background: var(--coar-background-neutral-secondary);
  padding: 0.125rem 0.375rem;
  border-radius: var(--coar-radius-xs);
}

.placeholder-text {
  font-size: 0.875rem;
  color: var(--coar-text-neutral-secondary);
  padding: 1.5rem 0;
  text-align: center;
}
</style>
