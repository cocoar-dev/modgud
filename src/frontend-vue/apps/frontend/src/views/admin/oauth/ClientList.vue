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
import ClientDetails from './ClientDetails.vue'

interface ClientRow {
  Id: string
  ClientId: string
  DisplayName?: string | null
  ClientType: string
  ConsentType: string
  Enabled?: boolean
  RedirectUris: string[]
  Permissions: string[]
  Roles: string[]
  CreatedAt?: string | null
}

interface PagedResponse<T> { Items: T[]; TotalCount: number }

const ui = useUI()
const modal = useModal()

const rows = ref<ClientRow[]>([])
const loadError = ref<string | null>(null)

const cellMenu = useContextMenu()
const selectedIds = ref<string[]>([])

async function load() {
  try {
    const http = useHttpClient('/api/admin/oauth/clients')
    const result = await http.setQueryParameter('pageSize', '200').get<PagedResponse<ClientRow>>()
    rows.value = result.Items
  } catch (e) {
    loadError.value = e instanceof HttpClientError
      ? `Failed to load OAuth clients (HTTP ${e.status}).`
      : 'Failed to load OAuth clients.'
  }
}

const rowsComp = computed(() => rows.value)

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-oauth-clients')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rowsComp)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => { if (event.data) openDetails(event.data.Id) })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) { event.api.deselectAll(); event.node.setSelected(true) }
    selectedIds.value = event.api.getSelectedRows().map((r: ClientRow) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    (col: any) => col.field('ClientId').header('Client ID').flex(1).minWidth(180),
    (col: any) => col.field('DisplayName').header('Display Name').flex(1),
    (col: any) => col.field('ClientType').header('Type').width(110),
    (col: any) => col.field('Enabled').header('Enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled === false ? 'No' : 'Yes'),
    (col: any) => col.field('RedirectUris').header('Redirect URIs').flex(1)
      .option('valueGetter', (p: any) => (p.data?.RedirectUris ?? []).length),
    (col: any) => col.field('Permissions').header('Permissions').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Permissions ?? []).length),
  ])

function openDetails(id: string) {
  modal.open(ClientDetails, { id }, { size: 'l', closeOnBackdropClick: true })
}

onMounted(() => {
  ui.set((ctx) => {
    ctx.header.title = 'OAuth Clients'
    ctx.header.subTitle = 'Registered OAuth client applications'
    ctx.header.icon = 'key-round'
    ctx.content.container = false
  })
  load()
})

onUnmounted(() => { ui.reset() })
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
