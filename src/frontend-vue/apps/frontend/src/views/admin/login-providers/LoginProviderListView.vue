<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { useUI } from '@/composables/useUI';
import type { LoginProviderListDto } from '@/core/models/login-provider.models';

const ui = useUI();

const router = useRouter();
const providers = ref<LoginProviderListDto[] | null>(null);
const isLoading = ref(true);
const error = ref('');

const { builder } = useDataGrid();

builder
  .columns([
    (col: any) => col.field('name').header('Name').flex(1).sortable(),
    (col: any) => col.field('displayName').header('Display Name').flex(1),
    (col: any) => col.field('type').header('Type').width(150).sortable(),
    (col: any) => col.field('description').header('Description').flex(2),
  ])
  .rowDataRef(providers)
  .rowId((params: any) => params.data?.id || '')
  .onRowClicked((event: any) => {
    if (event.data?.id) router.push(`/admin/login-providers/${event.data.id}`);
  });

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = 'Login Providers';
  ctx.header.subTitle = 'Manage authentication providers';
  ctx.content.scrollable = false;
  ctx.content.container = false;
});

onMounted(async () => {
  try {
    const result = await adminApi.getLoginProviders();
    providers.value = result.items;
  } catch {
    error.value = 'Failed to load login providers.';
  } finally {
    isLoading.value = false;
  }
});
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" @click="router.push('/admin/login-providers/create')">New Provider</CoarButton>
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
