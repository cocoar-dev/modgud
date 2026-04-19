<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarContextMenu,
  CoarMenuItem,
  CoarMenuDivider,
  useContextMenu,
  useDialog,
  useToast,
} from '@cocoar/vue-ui'
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
    (col: any) => col.field('Name').header('Name').flex(1).minWidth(180),
    (col: any) => col.field('ResourceType').header('Resource').width(160),
    (col: any) => col.field('Description').header('Description').flex(2),
    (col: any) => col.field('Permissions').header('Permissions').flex(2)
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
    title: 'Delete Permission Role',
    message: 'Delete this permission role? This cannot be undone.',
    confirmText: 'Delete',
    cancelText: 'Cancel',
    confirmVariant: 'danger',
  })
  const ok = await result.result
  if (!ok) return

  try {
    const http = useHttpClient('/api/admin/permission-roles')
    await http.addPath(id).delete()
    toast.success('Permission role deleted.')
    await store.loadAll()
  } catch (e) {
    const msg = e instanceof HttpClientError
      ? ((e.body as any)?.detail ?? (e.body as any)?.title ?? e.message)
      : 'Delete failed.'
    toast.error(String(msg))
  }
}

onMounted(() => {
  ui.set((ctx) => {
    ctx.header.title = 'Permission Roles'
    ctx.header.subTitle = 'Reusable permission bundles for authorization groups'
    ctx.header.icon = 'shield'
    ctx.content.container = false
    ctx.footer.show = true
    Object.assign(ctx.footer.button1, {
      visible: true,
      text: 'New Role',
      onClick: openCreate,
    })
  })
  store.initialize()
  store.loadAll()
})

onUnmounted(() => {
  ui.reset()
})
</script>

<template>
  <div class="list-wrap">
    <CoarDataGrid :builder="builder" show-search class="grid flex-1 min-h-0" bordered elevated />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem label="Edit" icon="pencil" @clicked="selectedIds[0] && openEdit(selectedIds[0])" />
      <CoarMenuItem label="New Role" icon="plus" @clicked="openCreate" />
      <CoarMenuDivider />
      <CoarMenuItem label="Delete" icon="trash-2" @clicked="confirmDeleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem label="New Role" icon="plus" @clicked="openCreate" />
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
