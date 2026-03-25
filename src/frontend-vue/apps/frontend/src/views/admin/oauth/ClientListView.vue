<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner, CoarContextMenu, CoarMenuItem, CoarMenuHeading, useContextMenu, useToast } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { ApiError } from '@/core/api/http';
import { useUI } from '@/composables/useUI';
import { useAdminHub } from '@/composables/useAdminHub';
import type { OAuthClient } from '@/core/models/oauth.models';

const ui = useUI();
const router = useRouter();
const toast = useToast();
const clients = ref<OAuthClient[] | null>(null);
const isLoading = ref(true);
const error = ref('');
const contextRow = ref<OAuthClient | null>(null);

const menu = useContextMenu();
const { builder } = useDataGrid();

builder
  .columns([
    (col: any) => col.field('clientId').header('Client ID').flex(1).sortable(),
    (col: any) => col.field('displayName').header('Display Name').flex(1),
    (col: any) => col.field('clientType').header('Type').width(120),
    (col: any) => col.field('consentType').header('Consent').width(120),
  ])
  .rowDataRef(clients)
  .rowId((params: any) => params.data?.id || '')
  .onRowDoubleClicked((event: any) => {
    if (event.data?.id) router.push(`/admin/oauth/clients/${event.data.id}`);
  })
  .onCellContextMenu((event: any) => { contextRow.value = event.data ?? null; menu.open(event.event); })
  .onViewportContextMenu(($event: MouseEvent) => { contextRow.value = null; menu.open($event); });

ui.set(ctx => {
  ctx.header.title = 'OAuth Clients';
  ctx.header.subTitle = 'Manage OAuth 2.0 / OIDC clients';
  ctx.content.scrollable = false;
  ctx.content.container = false;
});

const { onEntityChanged } = useAdminHub();

async function loadClients() {
  try { const result = await adminApi.getOAuthClients(); clients.value = result.items; }
  catch { error.value = 'Failed to load OAuth clients.'; }
  finally { isLoading.value = false; }
}

async function onDelete(client: OAuthClient) {
  if (!confirm(`Delete client "${client.clientId}"?`)) return;
  try { await adminApi.deleteOAuthClient(client.id); toast.success(`Client "${client.clientId}" deleted.`); loadClients(); }
  catch (err) { toast.error(err instanceof ApiError ? err.message : 'Failed to delete client.'); }
}

onMounted(loadClients);
onEntityChanged('oauth-client', loadClients);
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" @click="router.push('/admin/oauth/clients/create')">New Client</CoarButton>
    </div>
    <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>
    <CoarDataGrid v-else :builder="builder" />

    <CoarContextMenu :menu="menu">
      <CoarMenuItem label="New Client" icon="plus" @clicked="router.push('/admin/oauth/clients/create')" />
      <template v-if="contextRow">
        <CoarMenuHeading :label="contextRow.clientId" />
        <CoarMenuItem label="Edit" icon="pencil" @clicked="router.push(`/admin/oauth/clients/${contextRow.id}`)" />
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
