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
import ApiDetails from './ApiDetails.vue'

interface ApiRow {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Enabled: boolean
  Scopes: string[]
  UserClaims: string[]
  Secrets: unknown[]
}

interface ListResponse<T> { Items: T[]; TotalCount: number }

const ui = useUI()
const modal = useModal()

const rows = ref<ApiRow[]>([])
const loadError = ref<string | null>(null)

const cellMenu = useContextMenu()
const selectedIds = ref<string[]>([])

async function load() {
  try {
    const http = useHttpClient('/api/admin/oauth/apis')
    const result = await http.setQueryParameter('pageSize', '200').get<ListResponse<ApiRow>>()
    rows.value = result.Items
  } catch (e) {
    loadError.value = e instanceof HttpClientError
      ? `Failed to load OAuth APIs (HTTP ${e.status}).`
      : 'Failed to load OAuth APIs.'
  }
}

const rowsComp = computed(() => rows.value)

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-oauth-apis')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rowsComp)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => { if (event.data) openDetails(event.data.Id) })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) { event.api.deselectAll(); event.node.setSelected(true) }
    selectedIds.value = event.api.getSelectedRows().map((r: ApiRow) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    (col: any) => col.field('Name').header('Name').flex(1).minWidth(160),
    (col: any) => col.field('DisplayName').header('Display Name').flex(1),
    (col: any) => col.field('Description').header('Description').flex(2),
    (col: any) => col.field('Enabled').header('Enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled ? 'Yes' : 'No'),
    (col: any) => col.field('Scopes').header('Scopes').width(100)
      .option('valueGetter', (p: any) => (p.data?.Scopes ?? []).length),
    (col: any) => col.field('Secrets').header('Secrets').width(100)
      .option('valueGetter', (p: any) => (p.data?.Secrets ?? []).length),
  ])

function openDetails(id: string) {
  modal.open(ApiDetails, { id }, { size: 'm', closeOnBackdropClick: true })
}

onMounted(() => {
  ui.set((ctx) => {
    ctx.header.title = 'OAuth APIs'
    ctx.header.subTitle = 'Protected API resources'
    ctx.header.icon = 'server'
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
