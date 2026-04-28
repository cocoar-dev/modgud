<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { CoarDataGridPanel, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  CoarContextMenu,
  CoarMenuItem,
  CoarMenuDivider,
  useContextMenu,
  useDialog,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n, useL10n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useModal } from '@/composables/useModal'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import UserDetails from './UserDetails.vue'

interface UserRow {
  Id: string
  UserName: string
  Email?: string | null
  FirstName?: string | null
  LastName?: string | null
  IsActive: boolean
  CreatedAt?: string | null
  ModifiedAt?: string | null
  EmailConfirmed?: boolean
  TwoFactorEnabled?: boolean
}

interface PagedResponse<T> {
  Items: T[]
  TotalCount: number
  Page: number
  PageSize: number
}

const ui = useUI()
const modal = useModal()
const dialog = useDialog()
const toast = useToast()
const viewportMenu = useContextMenu()
const { t, language } = useI18n()
const { fmtDate } = useL10n()

const users = ref<UserRow[]>([])
const loading = ref(false)
const loadError = ref<string | null>(null)
const page = ref(1)
const pageSize = ref(100)
const totalCount = ref(0)

const cellMenu = useContextMenu()
const selectedIds = ref<string[]>([])

async function loadUsers() {
  loading.value = true
  loadError.value = null
  try {
    const http = useHttpClient('/api/admin/users')
    const result = await http
      .setQueryParameter('page', String(page.value))
      .setQueryParameter('pageSize', String(pageSize.value))
      .get<PagedResponse<UserRow>>()
    users.value = result.Items
    totalCount.value = result.TotalCount
  } catch (e) {
    loadError.value = e instanceof HttpClientError
      ? `Failed to load users (HTTP ${e.status}).`
      : 'Failed to load users.'
  } finally {
    loading.value = false
  }
}

const rows = computed(() => users.value)

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-users')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => {
    if (event.data) openDetails(event.data.Id)
  })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selectedIds.value = event.api.getSelectedRows().map((r: UserRow) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event: MouseEvent) => {
    viewportMenu.open($event)
  })
  .columns([
    (col: any) => col.field('UserName').header('Username', 'common.username').width(160),
    (col: any) => col.field('FirstName').header('First Name', 'admin.users.firstName').flex(1),
    (col: any) => col.field('LastName').header('Last Name', 'admin.users.lastName').flex(1),
    (col: any) => col.icon('IsActive', { color: '#16a34a', size: 's' })
      .option('valueGetter', (p: any) => p.data?.IsActive ? 'check' : '')
      .header('Active', 'common.active').width(80),
    (col: any) => col.field('Email').header('Email', 'common.email').flex(1),
    (col: any) => col.field('CreatedAt').header('Created', 'common.created').width(180)
      .option('valueGetter', (p: any) => p.data?.CreatedAt ? fmtDate(p.data.CreatedAt, true) : ''),
  ])

async function openDetails(id: string) {
  const result = await modal.open<{ deleted?: boolean } | undefined>(
    UserDetails, { id }, { size: 'm', closeOnBackdropClick: false },
  )
  if (result !== undefined) await loadUsers()
}

async function openCreate() {
  const result = await modal.open<unknown>(UserDetails, { id: 'create' }, { size: 'm' })
  if (result !== undefined) await loadUsers()
}

async function confirmDeleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const row = users.value.find(u => u.Id === id)
  const result = dialog.confirm({
    title: t('admin.users.deleteTitle', {}, 'Delete User'),
    message: t('admin.users.deleteMessage', { name: row?.UserName ?? id }, `Delete user "${row?.UserName ?? id}"? This soft-deletes the account.`),
    confirmText: t('common.delete', {}, 'Delete'),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmVariant: 'danger',
  })
  const ok = await result.result
  if (!ok) return
  try {
    const http = useHttpClient('/api/admin/users')
    await http.addPath(id).delete()
    toast.success(t('admin.users.deleted', {}, 'User deleted.'))
    await loadUsers()
  } catch (e) {
    const msg = e instanceof HttpClientError
      ? ((e.body as any)?.detail ?? (e.body as any)?.title ?? t('common.deleteFailed', {}, 'Delete failed.'))
      : t('common.deleteFailed', {}, 'Delete failed.')
    toast.error(String(msg))
  }
}

watch(language, () => {
  ui.set((ctx) => {
    ctx.header.title = t('admin.users.title', {}, 'Users')
    ctx.header.subTitle = t('admin.users.subtitle', {}, 'Manage user accounts')
    ctx.header.icon = 'users'
    ctx.content.container = false
  })
}, { immediate: true })

onMounted(() => loadUsers())

onUnmounted(() => {
  ui.reset()
})
</script>

<template>
  <div class="list-wrap">
    <div v-if="loadError" class="load-error">{{ loadError }}</div>
    <CoarDataGridPanel :builder="builder" class="flex-1 min-h-0" bordered elevated :search-placeholder="t('common.search', {}, 'Search...')">
      <template #actions>
        <CoarButton size="s" icon-start="plus" @click="openCreate">{{ t('common.create', {}, 'Create') }}</CoarButton>
      </template>
    </CoarDataGridPanel>

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.edit', {}, 'Edit')" icon="pencil" @clicked="selectedIds[0] && openDetails(selectedIds[0])" />
      <CoarMenuItem :label="t('admin.common.newUser', {}, 'New User')" icon="plus" @clicked="openCreate" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="confirmDeleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('admin.common.newUser', {}, 'New User')" icon="plus" @clicked="openCreate" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
.list-wrap {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
  padding: 1rem;
  width: 100%;
}
.grid {
  min-width: 0;
  flex: 1;
}
.load-error {
  margin-bottom: 8px;
  padding: 8px 12px;
  background: var(--coar-background-semantic-error-subtle, #fef2f2);
  color: var(--coar-text-semantic-error-bold, #b91c1c);
  border: 1px solid var(--coar-border-semantic-error, #fca5a5);
  border-radius: var(--coar-radius-m, 4px);
  font-size: 0.8125rem;
}
</style>
