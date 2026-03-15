<script setup lang="ts">
import { ref, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { CoarButton, CoarNote, CoarSpinner } from '@cocoar/vue-ui';
import { CoarDataGrid, useDataGrid } from '@cocoar/vue-data-grid';
import { adminApi } from '@/core/api/admin-api';
import { useUI } from '@/composables/useUI';
import type { User } from '@/core/models/auth.models';

const ui = useUI();

const router = useRouter();
const users = ref<User[] | null>(null);
const isLoading = ref(true);
const error = ref('');

const { builder } = useDataGrid();

builder
  .columns([
    (col: any) => col.field('userName').header('Username').sortable().pinned('left').flex(1),
    (col: any) => col.field('firstName').header('First Name').width(140),
    (col: any) => col.field('lastName').header('Last Name').width(140),
    (col: any) => col.field('email').header('Email').flex(1),
    (col: any) =>
      col
        .field('isActive')
        .header('Status')
        .width(120)
        .valueGetter((params: any) => {
          const user = params.data as User;
          if (!user) return '';
          if (!user.isActive) return 'Inactive';
          if (user.lockoutEnd) return 'Locked';
          return user.twoFactorEnabled ? 'Active (2FA)' : 'Active';
        }),
    (col: any) => col.date('createdAt').header('Created').width(130),
  ])
  .rowDataRef(users)
  .rowId((params: any) => params.data?.id || '')
  .onRowClicked((event: any) => {
    if (event.data?.id) router.push(`/admin/users/${event.data.id}`);
  });

// Set UI state synchronously (before first render)
ui.set(ctx => {
  ctx.header.title = 'Users';
  ctx.header.subTitle = 'Manage user accounts';
  ctx.content.scrollable = false;
  ctx.content.container = false;
  ctx.content.padding = true;
});

onMounted(async () => {
  try {
    const result = await adminApi.getUsers();
    users.value = result.items;
  } catch {
    error.value = 'Failed to load users.';
  } finally {
    isLoading.value = false;
  }
});
</script>

<template>
  <div class="list-page">
    <div class="page-actions">
      <CoarButton variant="primary" icon-start="user-plus" @click="router.push('/admin/users/create')">
        New User
      </CoarButton>
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
