<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import {
  CoarCard, CoarNote, CoarTextInput, CoarSelect, CoarButton, CoarSpinner,
  CoarTabGroup, CoarTab, CoarAvatar, CoarTag, useToast,
} from '@cocoar/vue-ui';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useDirtyGuard } from '@/composables/useDirtyGuard';
import { useUI } from '@/composables/useUI';
import type { User, Role, Group, GroupDetail } from '@/core/models/auth.models';

const route = useRoute();
const router = useRouter();
const { isDirty } = useDirtyGuard();
const ui = useUI();
const toast = useToast();

const id = computed(() => route.params.id as string | undefined);
const isEditMode = computed(() => !!id.value);

const activeTab = ref<string>('basic');
const name = ref('');
const description = ref('');

const group = ref<GroupDetail | null>(null);
const allUsers = ref<User[]>([]);
const allRoles = ref<Role[]>([]);
const allGroups = ref<Group[]>([]);

const isLoading = ref(false);
const isSaving = ref(false);
const error = ref('');

// ── Computed selectors (AlertHub pattern: filter out already-assigned) ──

const selectedUserId = ref('');
const availableUsers = computed(() => {
  if (!group.value) return [];
  const memberIds = new Set(group.value.memberIds || []);
  return allUsers.value
    .filter(u => !memberIds.has(u.id))
    .map(u => ({ value: u.id, label: `${u.firstName || ''} ${u.lastName || ''}`.trim() || u.userName }));
});

const selectedChildGroupId = ref('');
const availableChildGroups = computed(() => {
  if (!group.value) return [];
  const childIds = new Set(group.value.childGroupIds || []);
  return allGroups.value
    .filter(g => g.id !== id.value && !childIds.has(g.id))
    .map(g => ({ value: g.id, label: g.name }));
});

const selectedRoleId = ref('');
const roleOptions = computed(() =>
  allRoles.value.map(r => ({
    value: r.id,
    label: r.clientId ? `${r.name} (Client)` : r.name,
  }))
);

watch([name, description], () => { isDirty.value = true; });

ui.set(ctx => {
  ctx.header.title = isEditMode.value ? 'Edit Group' : 'Create Group';
  ctx.header.subTitle = isEditMode.value ? 'Manage group members, children, and role grants' : 'Create a new group';
  ctx.content.scrollable = true;
  ctx.footer.show = true;
  ctx.footer.button1.visible = true;
  ctx.footer.button1.text = 'Back';
  ctx.footer.button1.onClick = () => router.push('/admin/groups');
  ctx.footer.button2.visible = isEditMode.value;
  ctx.footer.button2.text = 'Archive';
  ctx.footer.button2.onClick = () => onDelete();
  ctx.footer.button3.visible = true;
  ctx.footer.button3.text = isEditMode.value ? 'Save Changes' : 'Create';
  ctx.footer.button3.onClick = () => onSubmit();
});

watch(isSaving, (val) => { ui.state.footer.button3.loading = val; });

async function onDelete() {
  if (!confirm('Are you sure you want to archive this group?')) return;
  try {
    await adminApi.deleteGroup(id.value!);
    isDirty.value = false;
    toast.success('Group archived.');
    router.push('/admin/groups');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to archive group.';
  }
}

async function loadGroupDetail() {
  if (!isEditMode.value) return;
  group.value = await adminApi.getGroup(id.value!);
}

onMounted(async () => {
  isLoading.value = true;
  try {
    const [usersResult, rolesResult, groupsResult] = await Promise.all([
      adminApi.getUsers(),
      adminApi.getRoles(),
      adminApi.getGroups(),
    ]);
    allUsers.value = usersResult.items;
    allRoles.value = rolesResult.items;
    allGroups.value = groupsResult.items;

    if (isEditMode.value) {
      await loadGroupDetail();
      name.value = group.value?.name || '';
      description.value = group.value?.description || '';
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
      await adminApi.updateGroup(id.value!, { name: name.value, description: description.value || undefined });
    } else {
      await adminApi.createGroup({ name: name.value, description: description.value || undefined });
    }
    isDirty.value = false;
    router.push('/admin/groups');
  } catch (err) {
    error.value = err instanceof ApiError ? err.message : 'Failed to save group.';
  } finally {
    isSaving.value = false;
  }
}

// ── Helpers ──
function getUserName(userId: string) {
  const user = allUsers.value.find(u => u.id === userId);
  if (!user) return userId;
  return `${user.firstName || ''} ${user.lastName || ''}`.trim() || user.userName;
}
function getGroupName(groupId: string) {
  return allGroups.value.find(g => g.id === groupId)?.name || groupId;
}
function getRoleName(roleId: string) {
  return allRoles.value.find(r => r.id === roleId)?.name || roleId;
}

// ── Member actions ──
async function addMember() {
  if (!selectedUserId.value) return;
  try {
    await adminApi.addGroupMember(id.value!, selectedUserId.value);
    selectedUserId.value = '';
    await loadGroupDetail();
    toast.success('Member added.');
  } catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to add member.'); }
}
async function removeMember(userId: string) {
  if (!confirm(`Remove ${getUserName(userId)}?`)) return;
  try {
    await adminApi.removeGroupMember(id.value!, userId);
    await loadGroupDetail();
    toast.success('Member removed.');
  } catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to remove member.'); }
}

// ── Child group actions ──
async function addChildGroup() {
  if (!selectedChildGroupId.value) return;
  try {
    await adminApi.addChildGroup(id.value!, selectedChildGroupId.value);
    selectedChildGroupId.value = '';
    await loadGroupDetail();
    toast.success('Child group added.');
  } catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to add child group.'); }
}
async function removeChildGroup(childId: string) {
  if (!confirm(`Remove ${getGroupName(childId)}?`)) return;
  try {
    await adminApi.removeChildGroup(id.value!, childId);
    await loadGroupDetail();
    toast.success('Child group removed.');
  } catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to remove child group.'); }
}

// ── Role grant actions ──
async function grantRole() {
  if (!selectedRoleId.value) return;
  const role = allRoles.value.find(r => r.id === selectedRoleId.value);
  try {
    await adminApi.grantGroupRole(id.value!, selectedRoleId.value, role?.clientId || undefined);
    selectedRoleId.value = '';
    await loadGroupDetail();
    toast.success('Role granted.');
  } catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to grant role.'); }
}
async function revokeRealmRole(roleId: string) {
  if (!confirm(`Revoke "${getRoleName(roleId)}"?`)) return;
  try {
    await adminApi.revokeGroupRole(id.value!, roleId);
    await loadGroupDetail();
    toast.success('Role revoked.');
  } catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to revoke role.'); }
}
async function revokeClientRole(roleId: string, clientId: string) {
  if (!confirm(`Revoke "${getRoleName(roleId)}"?`)) return;
  try {
    await adminApi.revokeGroupRole(id.value!, roleId, clientId);
    await loadGroupDetail();
    toast.success('Role revoked.');
  } catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to revoke role.'); }
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
              <CoarCard padding="l" class="form-card">
                <div class="form-group"><CoarTextInput v-model="name" label="Name" :required="true" /></div>
                <div class="form-group"><CoarTextInput v-model="description" label="Description" :rows="3" /></div>
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="members">
            <template #default>Members ({{ group?.memberIds?.length || 0 }})</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <div class="inline-add">
                  <div class="inline-add-field">
                    <CoarSelect v-model="selectedUserId" :options="availableUsers" label="Add Member" placeholder="Select user..." />
                  </div>
                  <CoarButton variant="primary" :disabled="!selectedUserId" @click="addMember">Add</CoarButton>
                </div>
                <div v-if="group?.memberIds?.length" class="item-list">
                  <div v-for="userId in group.memberIds" :key="userId" class="item-row">
                    <div class="item-info">
                      <CoarAvatar :name="getUserName(userId)" size="s" />
                      <span class="item-name">{{ getUserName(userId) }}</span>
                    </div>
                    <CoarButton variant="ghost" size="s" @click="removeMember(userId)">Remove</CoarButton>
                  </div>
                </div>
                <p v-else class="empty-text">No members yet.</p>
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="children">
            <template #default>Children ({{ group?.childGroupIds?.length || 0 }})</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <div class="inline-add">
                  <div class="inline-add-field">
                    <CoarSelect v-model="selectedChildGroupId" :options="availableChildGroups" label="Add Child Group" placeholder="Select group..." />
                  </div>
                  <CoarButton variant="primary" :disabled="!selectedChildGroupId" @click="addChildGroup">Add</CoarButton>
                </div>
                <div v-if="group?.childGroupIds?.length" class="item-list">
                  <div v-for="childId in group.childGroupIds" :key="childId" class="item-row">
                    <span class="item-name">{{ getGroupName(childId) }}</span>
                    <CoarButton variant="ghost" size="s" @click="removeChildGroup(childId)">Remove</CoarButton>
                  </div>
                </div>
                <p v-else class="empty-text">No child groups.</p>
              </CoarCard>
            </template>
          </CoarTab>

          <CoarTab v-if="isEditMode" id="roles">
            <template #default>Roles ({{ (group?.realmRoleGrants?.length || 0) + (group?.clientRoleGrants?.length || 0) }})</template>
            <template #content>
              <CoarCard padding="l" class="form-card">
                <div class="inline-add">
                  <div class="inline-add-field">
                    <CoarSelect v-model="selectedRoleId" :options="roleOptions" label="Grant Role" placeholder="Select role..." />
                  </div>
                  <CoarButton variant="primary" :disabled="!selectedRoleId" @click="grantRole">Grant</CoarButton>
                </div>
                <div v-if="(group?.realmRoleGrants?.length || 0) + (group?.clientRoleGrants?.length || 0) > 0" class="item-list">
                  <div v-for="grant in group.realmRoleGrants" :key="'r-' + grant.roleId" class="item-row">
                    <div class="item-info">
                      <span class="item-name">{{ getRoleName(grant.roleId) }}</span>
                      <CoarTag variant="info" size="s">Realm</CoarTag>
                    </div>
                    <CoarButton variant="ghost" size="s" @click="revokeRealmRole(grant.roleId)">Revoke</CoarButton>
                  </div>
                  <div v-for="grant in group.clientRoleGrants" :key="'c-' + grant.roleId + grant.clientId" class="item-row">
                    <div class="item-info">
                      <span class="item-name">{{ getRoleName(grant.roleId) }}</span>
                      <CoarTag variant="accent" size="s">Client</CoarTag>
                    </div>
                    <CoarButton variant="ghost" size="s" @click="revokeClientRole(grant.roleId, grant.clientId)">Revoke</CoarButton>
                  </div>
                </div>
                <p v-else class="empty-text">No role grants.</p>
              </CoarCard>
            </template>
          </CoarTab>
        </CoarTabGroup>
      </form>
    </template>
  </div>
</template>

<style scoped>
.form-group { margin-bottom: 1rem; }
.form-group:last-child { margin-bottom: 0; }
.form-card { margin-bottom: 1.5rem; }
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }

.inline-add { display: flex; align-items: flex-end; gap: 0.75rem; margin-bottom: 1.25rem; }
.inline-add-field { flex: 1; }

.item-list { display: flex; flex-direction: column; gap: 0.5rem; }
.item-row {
  display: flex; align-items: center; justify-content: space-between;
  padding: 0.625rem 0.875rem;
  border: 1px solid var(--coar-border-neutral-secondary);
  border-radius: var(--coar-radius-s);
}
.item-info { display: flex; align-items: center; gap: 0.625rem; }
.item-name { font-size: 0.875rem; font-weight: 500; color: var(--coar-text-neutral-primary); }
.empty-text { font-size: 0.875rem; color: var(--coar-text-neutral-secondary); text-align: center; padding: 1.5rem 0; }
</style>
