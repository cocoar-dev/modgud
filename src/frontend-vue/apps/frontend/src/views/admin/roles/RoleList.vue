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
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useModal } from '@/composables/useModal'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import RoleDetails from './RoleDetails.vue'

interface RoleRow {
  Id: string
  Name: string
  Description?: string | null
  DisplayName?: string | null
  Email?: string | null
  ClientId?: string | null
  Scopes: string[]
  CreatedAt?: string | null
}

interface ListResponse<T> {
  Items: T[]
  TotalCount: number
}

const ui = useUI()
const modal = useModal()
const dialog = useDialog()
const toast = useToast()
const { t, language } = useI18n()

const roles = ref<RoleRow[]>([])
const loadError = ref<string | null>(null)

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

async function load() {
  try {
    const http = useHttpClient('/api/admin/roles')
    const result = await http.get<ListResponse<RoleRow>>()
    roles.value = result.Items
  } catch (e) {
    loadError.value = e instanceof HttpClientError
      ? `Failed to load roles (HTTP ${e.status}).`
      : 'Failed to load roles.'
  }
}

const rows = computed(() => roles.value)

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-roles')
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
    selectedIds.value = event.api.getSelectedRows().map((r: RoleRow) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event: MouseEvent) => {
    viewportMenu.open($event)
  })
  .columns([
    (col: any) => col.field('Name').header('Name', 'common.name').flex(1).minWidth(160),
    (col: any) => col.field('DisplayName').header('Display Name', 'common.displayName').flex(1),
    (col: any) => col.field('Description').header('Description', 'common.description').flex(2),
    (col: any) => col.field('ClientId').header('Client', 'admin.roles.client').width(180)
      .option('valueGetter', (p: any) => p.data?.ClientId ?? 'realm'),
    (col: any) => col.field('Scopes').header('Scopes', 'admin.roles.scopes').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Scopes ?? []).join(', ')),
  ])

async function openDetails(id: string) {
  const result = await modal.open<unknown>(RoleDetails, { id }, { size: 'm' })
  if (result !== undefined) await load()
}

async function openCreate() {
  const result = await modal.open<unknown>(RoleDetails, { id: 'create' }, { size: 'm' })
  if (result !== undefined) await load()
}

async function confirmDeleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const row = roles.value.find(r => r.Id === id)
  const result = dialog.confirm({
    title: t('admin.roles.deleteTitle', {}, 'Delete Role'),
    message: t('admin.roles.deleteMessage', { name: row?.Name ?? id }, `Delete role "${row?.Name ?? id}"? This cannot be undone.`),
    confirmText: t('common.delete', {}, 'Delete'),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmVariant: 'danger',
  })
  const ok = await result.result
  if (!ok) return
  try {
    const http = useHttpClient('/api/admin/roles')
    await http.addPath(id).delete()
    toast.success(t('admin.roles.deleted', {}, 'Role deleted.'))
    await load()
  } catch (e) {
    const msg = e instanceof HttpClientError
      ? ((e.body as any)?.detail ?? (e.body as any)?.title ?? t('common.deleteFailed', {}, 'Delete failed.'))
      : t('common.deleteFailed', {}, 'Delete failed.')
    toast.error(String(msg))
  }
}

watch(language, () => {
  ui.set((ctx) => {
    ctx.header.title = t('admin.roles.title', {}, 'Roles')
    ctx.header.subTitle = t('admin.roles.subtitle', {}, 'OAuth identity roles')
    ctx.header.icon = 'shield-check'
    ctx.content.container = false
  })
}, { immediate: true })

onMounted(() => load())

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
      <CoarMenuItem :label="t('admin.common.newRole', {}, 'New Role')" icon="plus" @clicked="openCreate" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="confirmDeleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('admin.common.newRole', {}, 'New Role')" icon="plus" @clicked="openCreate" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
.list-wrap { display: flex; flex-direction: column; flex: 1; min-height: 0; min-width: 0; padding: 1rem; width: 100%; }
.grid { min-width: 0; flex: 1; }
.load-error {
  margin-bottom: 8px; padding: 8px 12px;
  background: var(--coar-background-semantic-error-subtle, #fef2f2);
  color: var(--coar-text-semantic-error-bold, #b91c1c);
  border: 1px solid var(--coar-border-semantic-error, #fca5a5);
  border-radius: var(--coar-radius-m, 4px);
  font-size: 0.8125rem;
}
</style>
