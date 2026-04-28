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
import { usePermissionRoleStore, type PermissionRoleDto } from '@/stores/permission-role.store'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import RoleEdit from './RoleEdit.vue'

const ui = useUI()
const store = usePermissionRoleStore()
const dialog = useDialog()
const modal = useModal()
const toast = useToast()
const { t, language } = useI18n()

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const roles = computed(() => store.entities as unknown as PermissionRoleDto[])

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-permission-roles')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(roles)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => {
    if (event.data) openEdit(event.data.Id)
  })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selectedIds.value = event.api.getSelectedRows().map((r: PermissionRoleDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event: MouseEvent) => {
    viewportMenu.open($event)
  })
  .columns([
    (col: any) => col.field('Name').header('Name', 'common.name').flex(1).minWidth(180),
    (col: any) => col.field('ResourceType').header('Resource', 'admin.permissionRoles.resource').width(160),
    (col: any) => col.field('Description').header('Description', 'common.description').flex(2),
    (col: any) => col.field('Permissions').header('Permissions', 'admin.permissionRoles.permissions').flex(2)
      .option('valueGetter', (p: any) => (p.data?.Permissions ?? []).join(', ')),
  ])

function openEdit(id: string) {
  modal.open(RoleEdit, { id }, { size: 'm' })
}

function openCreate() {
  modal.open(RoleEdit, { id: 'create' }, { size: 'm' })
}

async function confirmDeleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const result = dialog.confirm({
    title: t('admin.permissionRoles.deleteTitle', {}, 'Delete Permission Role'),
    message: t('admin.permissionRoles.deleteMessage', {}, 'Delete this permission role? This cannot be undone.'),
    confirmText: t('common.delete', {}, 'Delete'),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmVariant: 'danger',
  })
  const ok = await result.result
  if (!ok) return

  try {
    const http = useHttpClient('/api/admin/permission-roles')
    await http.addPath(id).delete()
    toast.success(t('admin.permissionRoles.deleted', {}, 'Permission role deleted.'))
    await store.loadAll()
  } catch (e) {
    const msg = e instanceof HttpClientError
      ? ((e.body as any)?.detail ?? (e.body as any)?.title ?? e.message)
      : t('common.deleteFailed', {}, 'Delete failed.')
    toast.error(String(msg))
  }
}

watch(language, () => {
  ui.set((ctx) => {
    ctx.header.title = t('admin.permissionRoles.title', {}, 'Permission Roles')
    ctx.header.subTitle = t('admin.permissionRoles.subtitle', {}, 'Reusable permission bundles for authorization groups')
    ctx.header.icon = 'shield'
    ctx.content.container = false
  })
}, { immediate: true })

onMounted(() => {
  store.initialize()
  store.loadAll()
})

onUnmounted(() => {
  ui.reset()
})
</script>

<template>
  <div class="list-wrap">
    <CoarDataGridPanel :builder="builder" class="flex-1 min-h-0" bordered elevated :search-placeholder="t('common.search', {}, 'Search...')">
      <template #actions>
        <CoarButton size="s" icon-start="plus" @click="openCreate">{{ t('common.create', {}, 'Create') }}</CoarButton>
      </template>
    </CoarDataGridPanel>

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.edit', {}, 'Edit')" icon="pencil" @clicked="selectedIds[0] && openEdit(selectedIds[0])" />
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
</style>
