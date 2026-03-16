<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { useUI } from '@/composables/useUI';
import type { Realm } from '@/core/models/auth.models';

const ui = useUI();
const router = useRouter();
const realms = ref<Realm[] | null>(null);
const isLoading = ref(true);
const error = ref('');

const { builder } = useDataGrid();

builder
  .columns([
    (col: any) => col.field('slug').header('Slug').flex(1).sortable(),
    (col: any) => col.field('displayName').header('Display Name').flex(2).sortable(),
    (col: any) => col.field('isActive').header('Active').width(100)
      .valueFormatter((p: any) => p.value ? 'Yes' : 'No'),
    (col: any) => col.field('needsSetup').header('Needs Setup').width(120)
      .valueFormatter((p: any) => p.value ? 'Yes' : 'No'),
    (col: any) => col.date('createdAt').header('Created').width(130),
  ])
  .rowDataRef(realms)
  .rowId((params: any) => params.data?.slug || '')
  .onRowClicked((event: any) => {
    if (event.data?.slug) router.push(`/admin/realms/${event.data.slug}`);
  });

ui.set(ctx => {
  ctx.header.title = 'Realms';
  ctx.header.subTitle = 'Manage identity realms';
  ctx.content.scrollable = false;
  ctx.content.container = false;
  ctx.content.padding = true;
});

onMounted(async () => {
  try {
    const result = await adminApi.getRealms();
    realms.value = result.items;
  } catch {
    error.value = 'Failed to load realms.';
  } finally {
    isLoading.value = false;
  }
});
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" @click="router.push('/admin/realms/create')">New Realm</CoarButton>
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
