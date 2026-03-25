<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner, CoarContextMenu, CoarMenuItem, CoarMenuHeading, useContextMenu, useToast } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useUI } from '@/composables/useUI';
import { useAdminHub } from '@/composables/useAdminHub';
import type { Role } from '@/core/models/auth.models';

const ui = useUI();
const router = useRouter();
const toast = useToast();
const roles = ref<Role[] | null>(null);
const isLoading = ref(true);
const error = ref('');
const contextRow = ref<Role | null>(null);

const menu = useContextMenu();
const { builder } = useDataGrid();

builder
  .columns([
    (col: any) => col.field('name').header('Name').flex(1).sortable(),
    (col: any) => col.field('description').header('Description').flex(2),
    (col: any) => col.date('createdAt').header('Created').width(130),
  ])
  .rowDataRef(roles)
  .rowId((params: any) => params.data?.id || '')
  .onRowDoubleClicked((event: any) => {
    if (event.data?.id) router.push(`/admin/roles/${event.data.id}`);
  })
  .onCellContextMenu((event: any) => { contextRow.value = event.data ?? null; menu.open(event.event); })
  .onViewportContextMenu(($event: MouseEvent) => { contextRow.value = null; menu.open($event); });

ui.set(ctx => {
  ctx.header.title = 'Roles';
  ctx.header.subTitle = 'Manage user roles';
  ctx.content.scrollable = false;
  ctx.content.container = false;
  ctx.content.padding = true;
});

const { onEntityChanged } = useAdminHub();

async function loadRoles() {
  try { const result = await adminApi.getRoles(); roles.value = result.items; }
  catch { error.value = 'Failed to load roles.'; }
  finally { isLoading.value = false; }
}

async function onDelete(role: Role) {
  if (!confirm(`Delete role "${role.name}"?`)) return;
  try { await adminApi.deleteRole(role.id); toast.success(`Role "${role.name}" deleted.`); loadRoles(); }
  catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to delete role.'); }
}

onMounted(loadRoles);
onEntityChanged('role', loadRoles);
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" @click="router.push('/admin/roles/create')">New Role</CoarButton>
    </div>
    <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>
    <CoarDataGrid v-else :builder="builder" />

    <CoarContextMenu :menu="menu">
      <CoarMenuItem label="New Role" icon="plus" @clicked="router.push('/admin/roles/create')" />
      <template v-if="contextRow">
        <CoarMenuHeading :label="contextRow.name" />
        <CoarMenuItem label="Edit" icon="pencil" @clicked="router.push(`/admin/roles/${contextRow.id}`)" />
        <CoarMenuItem label="Delete" icon="trash-2" @clicked="onDelete(contextRow)" />
      </template>
    </CoarContextMenu>
  </div>
</template>

<style scoped>
.list-page { display: flex; flex-direction: column; height: 100%; gap: 1rem; }
.page-actions { display: flex; justify-content: flex-end; }
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
</style>
