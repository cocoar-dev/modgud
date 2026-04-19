<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarContextMenu,
  CoarMenuItem,
  useContextMenu,
} from '@cocoar/vue-ui'
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

const roles = ref<RoleRow[]>([])
const loadError = ref<string | null>(null)

const cellMenu = useContextMenu()
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
  .columns([
    (col: any) => col.field('Name').header('Name').flex(1).minWidth(160),
    (col: any) => col.field('DisplayName').header('Display Name').flex(1),
    (col: any) => col.field('Description').header('Description').flex(2),
    (col: any) => col.field('ClientId').header('Client').width(180)
      .option('valueGetter', (p: any) => p.data?.ClientId ?? 'realm'),
    (col: any) => col.field('Scopes').header('Scopes').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Scopes ?? []).join(', ')),
  ])

function openDetails(id: string) {
  modal.open(RoleDetails, { id }, { size: 'm', closeOnBackdropClick: true })
}

onMounted(() => {
  ui.set((ctx) => {
    ctx.header.title = 'Roles'
    ctx.header.subTitle = 'OAuth identity roles'
    ctx.header.icon = 'shield-check'
    ctx.content.container = false
  })
  load()
})

onUnmounted(() => {
  ui.reset()
})
</script>

<template>
  <div class="list-wrap">
    <div v-if="loadError" class="load-error">{{ loadError }}</div>
    <CoarDataGrid :builder="builder" show-search class="grid flex-1 min-h-0" bordered elevated />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem label="View Details" icon="eye" @clicked="selectedIds[0] && openDetails(selectedIds[0])" />
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
