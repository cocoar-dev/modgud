<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton, useContextMenu, CoarContextMenu, CoarMenuItem, CoarMenuDivider } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { usePositionStore } from '@/stores/position.store'
import { useDraftListOverlay, type DraftRow } from '@/composables/useDraftStaging'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import type { PositionPrincipalDto } from '@/models/position'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = usePositionStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.positions.title', {}, 'Positions')
  ctx.header.icon = 'briefcase'
  ctx.content.container = false
}), { immediate: true })

const liveRows = computed(() => store.entities)

// ADR-0005: draft-merged roster (natural key = the lowercased account name).
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const rows = useDraftListOverlay<PositionPrincipalDto>({
  section: 'positions',
  rows: liveRows,
  matchLive: (row, e) => row.AccountName.trim().toLowerCase() === str(e.AccountName).trim().toLowerCase(),
  overlay: (row, e) => ({
    ...row,
    AccountName: str(e.AccountName) || row.AccountName,
    Purpose: str(e.Purpose) || row.Purpose,
    IsActive: e.IsActive !== false,
    TerminalPolicy: {
      ...row.TerminalPolicy,
      Enabled: (e.TerminalPolicy as Record<string, unknown> | undefined)?.Enabled === true,
    },
  }),
  synthesize: (key, e) => ({
    Id: `draft__${key}`,
    AccountName: str(e.AccountName) || key,
    Purpose: str(e.Purpose) || null,
    IsActive: e.IsActive !== false,
    TerminalPolicy: {
      Enabled: (e.TerminalPolicy as Record<string, unknown> | undefined)?.Enabled === true,
    },
  } as unknown as PositionPrincipalDto),
})

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => store.allLoaded && rows.value.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<DraftRow<PositionPrincipalDto>>(), { openable: true })
  .persistColumnState('admin-positions')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event) => {
    if (event.data) navigateToModal(event.data.Id)
  })
  .onCellContextMenu((event) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selectedIds.value = event.api.getSelectedRows().map((r: PositionPrincipalDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    (col) => col.field('AccountName').header('Account name', 'admin.positions.accountName').width(220).pinned('left').cellClass('account-name-cell'),
    (col) => col.field('Purpose').header('Purpose', 'admin.positions.purpose').flex(1),
    (col) => col.field('DraftStaged').header('Draft', 'admin.realmConfig.gridCol')
      .valueGetter((p: any) => p.data?.DraftStaged === 'create'
        ? t('admin.realmConfig.gridTag.create', {}, 'Staged (new)')
        : p.data?.DraftStaged === 'update'
          ? t('admin.realmConfig.gridTag.update', {}, 'Staged')
          : '')
      .width(120)
      .classRule('draft-staged-cell', (p: any) => !!p.data?.DraftStaged),
    (col) => col.icon('TerminalPolicy', { color: '#0284c7', size: 's' })
      .option('valueGetter', (p: any) => p.data?.TerminalPolicy?.Enabled ? 'monitor-smartphone' : '')
      .option('tooltipValueGetter', () => null)
      .header('Terminals', 'admin.positions.terminalsEnabled').width(100),
    (col) => col.icon('IsActive', { color: '#16a34a', size: 's' })
      .option('valueGetter', (p: any) => p.data?.IsActive ? 'check' : '')
      .option('tooltipValueGetter', () => null)
      .header('Active', 'admin.users.active').width(80),
  ])

async function deleteRows() {
  if (selectedIds.value.length > 0 && confirm(t('common.confirmDelete', {}, 'Really delete?'))) {
    await store.deleteEntities(selectedIds.value)
  }
}

onMounted(() => {
  store.initialize()
})
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">{{ t('common.create', {}, 'Create') }}</CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="briefcase"
      :title="t('admin.positions.title', {}, 'Positions')"
      :description="t('admin.positions.emptyHint', {}, 'A position is the business identity of a staffed role — like a gate porter for one customer — filled by changing people on shared terminals. Create one to give that role its own permissions and audit identity.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteRows" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
:deep(.draft-staged-cell) {
  color: var(--coar-text-semantic-info, #2563eb);
  font-weight: 600;
}

:deep(.account-name-cell) {
  font-weight: 600;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
</style>
