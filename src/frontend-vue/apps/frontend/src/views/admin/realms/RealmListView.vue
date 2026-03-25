<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner, CoarContextMenu, CoarMenuItem, CoarMenuHeading, useContextMenu, useToast } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useUI } from '@/composables/useUI';
import { useAdminHub } from '@/composables/useAdminHub';
import type { Realm } from '@/core/models/auth.models';

const ui = useUI();
const router = useRouter();
const toast = useToast();
const realms = ref<Realm[] | null>(null);
const isLoading = ref(true);
const error = ref('');
const contextRow = ref<Realm | null>(null);

const menu = useContextMenu();
const { builder } = useDataGrid();

builder
  .columns([
    (col: any) => col.field('slug').header('Slug').flex(1).sortable(),
    (col: any) => col.field('displayName').header('Display Name').flex(2).sortable(),
    (col: any) => col.field('isActive').header('Active').width(100).valueFormatter((p: any) => p.value ? 'Yes' : 'No'),
    (col: any) => col.field('needsSetup').header('Needs Setup').width(120).valueFormatter((p: any) => p.value ? 'Yes' : 'No'),
    (col: any) => col.date('createdAt').header('Created').width(130),
  ])
  .rowDataRef(realms)
  .rowId((params: any) => params.data?.slug || '')
  .onRowDoubleClicked((event: any) => {
    if (event.data?.slug) router.push(`/admin/realms/${event.data.slug}`);
  })
  .onCellContextMenu((event: any) => { contextRow.value = event.data ?? null; menu.open(event.event); })
  .onViewportContextMenu(($event: MouseEvent) => { contextRow.value = null; menu.open($event); });

ui.set(ctx => {
  ctx.header.title = 'Realms';
  ctx.header.subTitle = 'Manage identity realms';
  ctx.content.scrollable = false;
  ctx.content.container = false;
  ctx.content.padding = true;
});

const { onEntityChanged } = useAdminHub();

async function loadRealms() {
  try { const result = await adminApi.getRealms(); realms.value = result.items; }
  catch { error.value = 'Failed to load realms.'; }
  finally { isLoading.value = false; }
}

async function onDelete(realm: Realm) {
  if (!confirm(`Delete realm "${realm.slug}"?`)) return;
  try { await adminApi.deleteRealm(realm.slug); toast.success(`Realm "${realm.slug}" deleted.`); loadRealms(); }
  catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to delete realm.'); }
}

onMounted(loadRealms);
onEntityChanged('realm', loadRealms);
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" @click="router.push('/admin/realms/create')">New Realm</CoarButton>
    </div>
    <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>
    <CoarDataGrid v-else :builder="builder" />

    <CoarContextMenu :menu="menu">
      <CoarMenuItem label="New Realm" icon="plus" @clicked="router.push('/admin/realms/create')" />
      <template v-if="contextRow">
        <CoarMenuHeading :label="contextRow.slug" />
        <CoarMenuItem label="Edit" icon="pencil" @clicked="router.push(`/admin/realms/${contextRow.slug}`)" />
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
