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
import {
  useAuthorizationGroupStore,
  type AuthorizationGroupDto,
} from '@/stores/authorization-group.store'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import GroupEdit from './GroupEdit.vue'

const ui = useUI()
const store = useAuthorizationGroupStore()
const dialog = useDialog()
const modal = useModal()
const toast = useToast()

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const groups = computed(() => store.entities as unknown as AuthorizationGroupDto[])

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-authorization-groups')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(groups)
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
    selectedIds.value = event.api.getSelectedRows().map((r: AuthorizationGroupDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event: MouseEvent) => {
    viewportMenu.open($event)
  })
  .columns([
    (col: any) => col.field('Name').header('Name').flex(1).minWidth(180),
    (col: any) => col.field('MembershipMode').header('Mode').width(110),
    (col: any) => col.field('MemberIds').header('Members').width(100)
      .option('valueGetter', (p: any) => (p.data?.MemberIds ?? []).length),
    (col: any) => col.field('RoleIds').header('Roles').width(90)
      .option('valueGetter', (p: any) => (p.data?.RoleIds ?? []).length),
    (col: any) => col.field('Email').header('Email').flex(1),
    (col: any) => col.field('MembershipLastError').header('Script Error').flex(1)
      .option('cellClass', (p: any) => p.data?.MembershipLastError ? 'cell-error' : ''),
  ])

function openEdit(id: string) {
  modal.open(GroupEdit, { id }, { size: 'l' })
}

function openCreate() {
  modal.open(GroupEdit, { id: 'create' }, { size: 'l' })
}

async function confirmDeleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const result = dialog.confirm({
    title: 'Delete Authorization Group',
    message: 'Delete this group? This cannot be undone.',
    confirmText: 'Delete',
    cancelText: 'Cancel',
    confirmVariant: 'danger',
  })
  const ok = await result.result
  if (!ok) return

  try {
    const http = useHttpClient('/api/admin/authorization-groups')
    await http.addPath(id).delete()
    toast.success('Group deleted.')
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
    ctx.header.title = 'Authorization Groups'
    ctx.header.subTitle = 'Group members, roles, and access scripts'
    ctx.header.icon = 'users-round'
    ctx.content.container = false
    ctx.footer.show = true
    Object.assign(ctx.footer.button1, {
      visible: true,
      text: 'New Group',
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
      <CoarMenuItem label="New Group" icon="plus" @clicked="openCreate" />
      <CoarMenuDivider />
      <CoarMenuItem label="Delete" icon="trash-2" @clicked="confirmDeleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem label="New Group" icon="plus" @clicked="openCreate" />
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

:deep(.cell-error) {
  color: var(--coar-text-semantic-error-bold, #b91c1c);
  font-weight: 500;
}
</style>
