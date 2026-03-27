<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner, CoarContextMenu, CoarMenuItem, CoarMenuHeading, useContextMenu, useToast } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useUI } from '@/composables/useUI';
import { useAdminHub } from '@/composables/useAdminHub';
import type { Group } from '@/core/models/auth.models';

const ui = useUI();
const router = useRouter();
const toast = useToast();
const groups = ref<Group[] | null>(null);
const isLoading = ref(true);
const error = ref('');
const contextRow = ref<Group | null>(null);

const menu = useContextMenu();
const { builder } = useDataGrid();

builder
  .columns([
    (col: any) => col.field('name').header('Name').flex(1).sortable(),
    (col: any) => col.field('description').header('Description').flex(2),
    (col: any) => col.field('memberCount').header('Members').width(100),
    (col: any) => col.field('childGroupCount').header('Children').width(100),
    (col: any) => col.field('roleGrantCount').header('Grants').width(100),
    (col: any) => col.date('createdAt').header('Created').width(130),
  ])
  .rowDataRef(groups)
  .rowId((params: any) => params.data?.id || '')
  .onRowDoubleClicked((event: any) => {
    if (event.data?.id) router.push(`/admin/groups/${event.data.id}`);
  })
  .onCellContextMenu((event: any) => { contextRow.value = event.data ?? null; menu.open(event.event); })
  .onViewportContextMenu(($event: MouseEvent) => { contextRow.value = null; menu.open($event); });

ui.set(ctx => {
  ctx.header.title = 'Groups';
  ctx.header.subTitle = 'Manage organizational groups';
  ctx.content.scrollable = false;
  ctx.content.container = false;
  ctx.content.padding = true;
});

const { onEntityChanged } = useAdminHub();

async function loadGroups() {
  try { const result = await adminApi.getGroups(); groups.value = result.items; }
  catch { error.value = 'Failed to load groups.'; }
  finally { isLoading.value = false; }
}

async function onDelete(group: Group) {
  if (!confirm(`Archive group "${group.name}"?`)) return;
  try { await adminApi.deleteGroup(group.id); toast.success(`Group "${group.name}" archived.`); loadGroups(); }
  catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to archive group.'); }
}

onMounted(loadGroups);
onEntityChanged('group', loadGroups);
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" @click="router.push('/admin/groups/create')">New Group</CoarButton>
    </div>
    <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>
    <CoarDataGrid v-else :builder="builder" />

    <CoarContextMenu :menu="menu">
      <CoarMenuItem label="New Group" icon="plus" @clicked="router.push('/admin/groups/create')" />
      <template v-if="contextRow">
        <CoarMenuHeading :label="contextRow.name" />
        <CoarMenuItem label="Edit" icon="pencil" @clicked="router.push(`/admin/groups/${contextRow.id}`)" />
        <CoarMenuItem label="Archive" icon="trash-2" @clicked="onDelete(contextRow)" />
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
