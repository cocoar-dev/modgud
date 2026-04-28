<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { CoarDataGridPanel, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarContextMenu,
  CoarMenuItem,
  useContextMenu,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useModal } from '@/composables/useModal'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import ScopeDetails from './ScopeDetails.vue'

interface ScopeRow {
  Id: string
  Name: string
  DisplayName?: string | null
  Description?: string | null
  Resources: string[]
  Enabled?: boolean
  Required?: boolean
  Emphasize?: boolean
  UserClaims: string[]
}

interface ListResponse<T> { Items: T[]; TotalCount: number }

const ui = useUI()
const modal = useModal()
const { t, language } = useI18n()

const rows = ref<ScopeRow[]>([])
const loadError = ref<string | null>(null)

const cellMenu = useContextMenu()
const selectedIds = ref<string[]>([])

async function load() {
  try {
    const http = useHttpClient('/api/admin/oauth/scopes')
    const result = await http.setQueryParameter('pageSize', '200').get<ListResponse<ScopeRow>>()
    rows.value = result.Items
  } catch (e) {
    loadError.value = e instanceof HttpClientError
      ? `Failed to load OAuth scopes (HTTP ${e.status}).`
      : 'Failed to load OAuth scopes.'
  }
}

const rowsComp = computed(() => rows.value)

const builder = CoarGridBuilder.create()
  .persistColumnState('admin-oauth-scopes')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rowsComp)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => { if (event.data) openDetails(event.data.Id) })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) { event.api.deselectAll(); event.node.setSelected(true) }
    selectedIds.value = event.api.getSelectedRows().map((r: ScopeRow) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    (col: any) => col.field('Name').header('Name', 'common.name').flex(1).minWidth(160),
    (col: any) => col.field('DisplayName').header('Display Name', 'common.displayName').flex(1),
    (col: any) => col.field('Description').header('Description', 'common.description').flex(2),
    (col: any) => col.field('Resources').header('Resources', 'admin.oauth.resources').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Resources ?? []).join(', ')),
    (col: any) => col.field('Enabled').header('Enabled', 'common.enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled === false ? 'No' : 'Yes'),
  ])

function openDetails(id: string) {
  modal.open(ScopeDetails, { id }, { size: 'm', closeOnBackdropClick: true })
}

watch(language, () => {
  ui.set((ctx) => {
    ctx.header.title = t('admin.oauth.scopesTitle', {}, 'OAuth Scopes')
    ctx.header.subTitle = t('admin.oauth.scopesSubtitle', {}, 'Scopes available to clients')
    ctx.header.icon = 'scan-line'
    ctx.content.container = false
  })
}, { immediate: true })

onMounted(() => load())

onUnmounted(() => { ui.reset() })
</script>

<template>
  <div class="list-wrap">
    <div v-if="loadError" class="load-error">{{ loadError }}</div>
    <CoarDataGridPanel :builder="builder" class="flex-1 min-h-0" bordered elevated :search-placeholder="t('common.search', {}, 'Search...')" />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('admin.common.viewDetails', {}, 'View Details')" icon="eye" @clicked="selectedIds[0] && openDetails(selectedIds[0])" />
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
