<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { useUI } from '@/composables/useUI';
import type { OAuthClient } from '@/core/models/oauth.models';

const ui = useUI();

const router = useRouter();
const clients = ref<OAuthClient[] | null>(null);
const isLoading = ref(true);
const error = ref('');

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
  .onRowClicked((event: any) => {
    if (event.data?.id) router.push(`/admin/oauth/clients/${event.data.id}`);
  });

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = 'OAuth Clients';
  ctx.header.subTitle = 'Manage OAuth 2.0 / OIDC clients';
  ctx.content.scrollable = false;
  ctx.content.container = false;
});

onMounted(async () => {
  try {
    const result = await adminApi.getOAuthClients();
    clients.value = result.items;
  } catch {
    error.value = 'Failed to load OAuth clients.';
  } finally {
    isLoading.value = false;
  }
});
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" @click="router.push('/admin/oauth/clients/create')">New Client</CoarButton>
    </div>
    <CoarNote v-if="error" variant="error" padding="s" class="mb-3">{{ error }}</CoarNote>
    <div v-if="isLoading" class="centered"><CoarSpinner size="l" /></div>
    <CoarDataGrid v-else :builder="builder" />
  </div>
</template>

<style scoped>
.list-page { display: flex; flex-direction: column; height: 100%; gap: 1rem; }
.page-actions { display: flex; justify-content: flex-end; }
.mb-3 { margin-bottom: 0.75rem; }
.centered { display: flex; justify-content: center; padding: 3rem; }
</style>
