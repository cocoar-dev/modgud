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
import RealmDetails from './RealmDetails.vue'

interface RealmRow {
  Id: string
  Slug: string
  DisplayName: string
  Description?: string | null
  Domains: string[]
  CanManageTenants: boolean
  IsActive: boolean
  NeedsSetup: boolean
  CreatedAt?: string | null
}

interface ListResponse<T> { Items: T[]; TotalCount: number }

const ui = useUI()
const modal = useModal()

const rows = ref<RealmRow[]>([])
const loadError = ref<string | null>(null)

const cellMenu = useContextMenu()
const selectedSlugs = ref<string[]>([])

async function load() {
  try {
    const http = useHttpClient('/api/admin/realms')
    const result = await http.get<ListResponse<RealmRow>>()
    rows.value = result.Items
  } catch (e) {
    loadError.value = e instanceof HttpClientError
      ? `Failed to load realms (HTTP ${e.status}).`
      : 'Failed to load realms.'
  }
}

const rowsComp = computed(() => rows.value)

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-realms')
  .option('getRowId', (p: any) => p.data.Slug)
  .rowDataRef(rowsComp)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => { if (event.data) openDetails(event.data.Slug) })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) { event.api.deselectAll(); event.node.setSelected(true) }
    selectedSlugs.value = event.api.getSelectedRows().map((r: RealmRow) => r.Slug)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    (col: any) => col.field('Slug').header('Slug').width(160),
    (col: any) => col.field('DisplayName').header('Display Name').flex(1).minWidth(180),
    (col: any) => col.field('Description').header('Description').flex(2),
    (col: any) => col.field('Domains').header('Domains').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Domains ?? []).join(', ')),
    (col: any) => col.field('IsActive').header('Active').width(90)
      .option('valueGetter', (p: any) => p.data?.IsActive ? 'Yes' : 'No'),
    (col: any) => col.field('CanManageTenants').header('Tenants').width(100)
      .option('valueGetter', (p: any) => p.data?.CanManageTenants ? 'Yes' : 'No'),
  ])

function openDetails(slug: string) {
  modal.open(RealmDetails, { slug }, { size: 'm', closeOnBackdropClick: true })
}

onMounted(() => {
  ui.set((ctx) => {
    ctx.header.title = 'Realms'
    ctx.header.subTitle = 'Tenants served by this identity provider'
    ctx.header.icon = 'globe'
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
      <CoarMenuItem label="View Details" icon="eye" @clicked="selectedSlugs[0] && openDetails(selectedSlugs[0])" />
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
