<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import {
  CoarCard, CoarNote, CoarTextInput, CoarPasswordInput,
  CoarCheckbox, CoarSpinner, CoarTabGroup, CoarTab, useToast,
} from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import DualListSelector from '@/components/DualListSelector.vue';
import ClaimsGrid from '@/components/ClaimsGrid.vue';
import type { User, Role } from '@/core/models/auth.models';
import type { DualListItem } from '@/components/DualListSelector.vue';
import type { Claim } from '@/components/ClaimsGrid.vue';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();

const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);

const user = ref<User | null>(null);
const roles = ref<Role[]>([]);
const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

// Tabs
const activeTab = ref<string>('basic');

// Form fields
const userName = ref('');
const email = ref('');
const password = ref('');
const firstName = ref('');
const lastName = ref('');
const phoneNumber = ref('');
const selectedRoles = ref<string[]>([]);
const isActive = ref(true);
const lockoutEnabled = ref(true);
const emailConfirmed = ref(false);
const phoneNumberConfirmed = ref(false);
const twoFactorEnabled = ref(false);
const expiresAt = ref('');
const claims = ref<Claim[]>([]);

const roleItems = computed<DualListItem[]>(() =>
  roles.value.map((r) => ({ id: r.id, name: r.name, displayName: r.description })),
);

// Mark dirty when fields change
watch([firstName, lastName, email, phoneNumber, selectedRoles, isActive, lockoutEnabled, emailConfirmed, phoneNumberConfirmed, twoFactorEnabled, password, expiresAt, claims], () => {
  isDirty.value = true;
}, { deep: true });

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit User' : 'Create User';
  ctx.header.subTitle = isEditMode.value ? 'Update user information' : 'Create a new user account';
  ctx.content.scrollable = true;
  ctx.content.padding = false;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/users');
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
  if (!confirm('Are you sure you want to delete this user? The account will be deactivated and data anonymized after the retention period.')) return;
  try {
    await adminApi.softDeleteUser(id.value!);
    isDirty.value = false;
    toast.success('User deleted.');
    router.push('/admin/users');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to delete user.';
  }
}

onMounted(async () => {
  isLoading.value = true;
  error.value = '';
  try {
    const [rolesResult, userData] = await Promise.all([
      adminApi.getRoles(),
      isEditMode.value ? adminApi.getUser(id.value!) : Promise.resolve(null),
    ]);

    roles.value = rolesResult.items;

    if (userData) {
      user.value = userData;
      userName.value = userData.userName;
      email.value = userData.email || '';
      firstName.value = userData.firstName || '';
      lastName.value = userData.lastName || '';
      phoneNumber.value = userData.phoneNumber || '';
      selectedRoles.value = userData.roles;
      isActive.value = userData.isActive;
      lockoutEnabled.value = userData.lockoutEnabled;
      emailConfirmed.value = userData.emailConfirmed;
      phoneNumberConfirmed.value = userData.phoneNumberConfirmed;
      twoFactorEnabled.value = userData.twoFactorEnabled;
      // TODO: load expiresAt when backend supports it
      // TODO: load claims when backend supports it (e.g. adminApi.getUserClaims(id))
    }
  } catch {
    error.value = 'Failed to load data.';
  } finally {
    isLoading.value = false;
    // Reset dirty after initial load
    setTimeout(() => { isDirty.value = false; }, 0);
  }
});

async function onSubmit() {
  isSaving.value = true;
  error.value = '';
  try {
    if (isEditMode.value) {
      await adminApi.updateUser(id.value!, {
        email: email.value || null,
        firstName: firstName.value || null,
        lastName: lastName.value || null,
        phoneNumber: phoneNumber.value || null,
        roles: selectedRoles.value,
        isActive: isActive.value,
        lockoutEnabled: lockoutEnabled.value,
        emailConfirmed: emailConfirmed.value,
        phoneNumberConfirmed: phoneNumberConfirmed.value,
        twoFactorEnabled: twoFactorEnabled.value,
      });
      // TODO: save claims when backend supports it (e.g. adminApi.updateUserClaims(id, claims))
    } else {
      await adminApi.createUser({
        userName: userName.value,
        password: password.value,
        email: email.value || undefined,
        firstName: firstName.value || undefined,
        lastName: lastName.value || undefined,
        phoneNumber: phoneNumber.value || undefined,
        roles: selectedRoles.value,
        isActive: isActive.value,
        lockoutEnabled: lockoutEnabled.value,
      });
    }
    isDirty.value = false;
    router.push('/admin/users');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save user.';
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
                      <CoarTextInput v-model="userName" label="Username" :required="true" :disabled="isEditMode" />
                    </div>
                    <div class="form-row-2">
                      <CoarTextInput v-model="firstName" label="First Name" />
                      <CoarTextInput v-model="lastName" label="Last Name" />
                    </div>
                    <div class="form-row-inline">
                      <div class="form-row-inline-field">
                        <CoarTextInput v-model="email" label="Email" />
                      </div>
                      <div v-if="isEditMode" class="form-row-inline-check">
                        <CoarCheckbox v-model="emailConfirmed" label="Confirmed" />
                      </div>
                    </div>
                    <div class="form-row-inline">
                      <div class="form-row-inline-field">
                        <CoarTextInput v-model="phoneNumber" label="Phone Number" />
                      </div>
                      <div v-if="isEditMode" class="form-row-inline-check">
                        <CoarCheckbox v-model="phoneNumberConfirmed" label="Confirmed" />
                      </div>
                    </div>
                    <div v-if="!isEditMode" class="form-group">
                      <CoarPasswordInput v-model="password" label="Password" :required="true" />
                    </div>

                    <!-- Roles multi-select for create mode (no tabs) -->
                    <div v-if="!isEditMode" class="form-group">
                      <label class="field-label">Roles</label>
                      <DualListSelector
                        v-model="selectedRoles"
                        :items="roleItems"
                        assigned-label="Selected Roles"
                        available-label="Available Roles"
                      />
                    </div>
                  </CoarCard>
                </div>

                <!-- Right sidebar: Options -->
                <div class="form-sidebar">
                  <CoarCard padding="l" class="form-card">
                    <h2 class="section-title">Options</h2>
                    <div class="sidebar-checks">
                      <CoarCheckbox v-model="isActive" label="Active" />
                      <CoarCheckbox v-if="isEditMode" v-model="twoFactorEnabled" label="Two-Factor Authentication" />
                      <CoarCheckbox v-model="lockoutEnabled" label="Lockout Enabled" />
                    </div>
                    <div class="form-group mt-3">
                      <CoarTextInput v-model="expiresAt" label="Expires At" placeholder="YYYY-MM-DD" />
                    </div>
                  </CoarCard>
                </div>
              </div>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="roles">
            <template #default>Roles</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <h2 class="section-title">Role Membership</h2>
                <DualListSelector
                  v-model="selectedRoles"
                  :items="roleItems"
                  assigned-label="Assigned Roles"
                  available-label="Available Roles"
                  filter-placeholder="Filter roles..."
                />
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="claims">
            <template #default>Claims</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <h2 class="section-title">User Claims</h2>
                <CoarNote variant="info" padding="s" class="mb-3">
                  Claims will be persisted once the backend API supports user claim management.
                </CoarNote>
                <ClaimsGrid v-model="claims" />
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

/* Form fields */
.form-group { margin-bottom: 1rem; }
.form-group:last-child { margin-bottom: 0; }
.form-row-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem; margin-bottom: 1rem; }
.form-row-inline { display: flex; align-items: flex-end; gap: 0.75rem; margin-bottom: 1rem; }
.form-row-inline-field { flex: 1; min-width: 0; }
.form-row-inline-check { flex-shrink: 0; padding-bottom: 0.5rem; }

.field-label { display: block; font-size: 0.8125rem; font-weight: 600; color: var(--coar-text-neutral-secondary); margin-bottom: 0.5rem; }

.sidebar-checks { display: flex; flex-direction: column; gap: 0.625rem; }

.form-actions { display: flex; gap: 0.75rem; }
.mb-3 { margin-bottom: 0.75rem; }
.mt-3 { margin-top: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
.centered-sm { display: flex; justify-content: center; padding: 1rem; }

/* Checkboxes */
.checkboxes { display: flex; flex-direction: column; gap: 0.625rem; margin-bottom: 1.5rem; }

</style>
