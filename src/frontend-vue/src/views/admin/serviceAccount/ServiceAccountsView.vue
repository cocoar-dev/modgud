<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton, useContextMenu, CoarContextMenu, CoarMenuItem, CoarMenuDivider } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useServiceAccountStore } from '@/stores/serviceAccount.store'
import { useUI } from '@/composables/useUI'
import { useExportSelectionMenu } from '@/composables/useExportSelectionMenu'
import { useGridLocale } from '@/composables/useGridLocale'
import { draftStagedColumn, useDraftListOverlay, useDraftStaging, type DraftRow } from '@/composables/useDraftStaging'
import type { ServiceAccountDto } from '@/models/serviceAccount'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useServiceAccountStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.serviceAccounts.title', {}, 'Service Accounts')
  ctx.header.icon = 'cpu'
  ctx.content.container = false
}), { immediate: true })

const liveRows = computed(() => store.entities)

// ADR-0017: draft-merged roster (natural key = the lowercased account name).
const staging = useDraftStaging('serviceAccounts')
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const rows = useDraftListOverlay<ServiceAccountDto>({
  section: 'serviceAccounts',
  rows: liveRows,
  liveKey: (row) => row.AccountName.trim().toLowerCase(),
  matchLive: (row, e) => row.AccountName.trim().toLowerCase() === str(e.AccountName).toLowerCase(),
  overlay: (row, e) => ({
    ...row,
    AccountName: str(e.AccountName) || row.AccountName,
    Purpose: e.Purpose === null ? null : (str(e.Purpose) || row.Purpose),
    IsActive: typeof e.IsActive === 'boolean' ? e.IsActive : row.IsActive,
  }),
  synthesize: (key, e) => ({
    Id: `draft__${key}`,
    AccountName: str(e.AccountName) || key,
    Purpose: str(e.Purpose) || null,
    IsActive: e.IsActive !== false,
  } as unknown as ServiceAccountDto),
})

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => store.allLoaded && rows.value.length === 0)

// Service accounts export with their credentials (never a secret); the manifest
// key is the lowercased account name.
const { exportMenuVisible, exportMenuLabel, exportMenuToggle } = useExportSelectionMenu('serviceAccounts',
  computed(() => {
    const row = rows.value.find((r) => r.Id === selectedIds.value[0])
    if (!row || row.DraftStaged === 'create') return null
    return row.AccountName.trim().toLowerCase()
  }))

const builder = applyListGridDefaults(CoarGridBuilder.create<DraftRow<ServiceAccountDto>>(), { openable: true })
  .persistColumnState('admin-service-accounts')
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
    selectedIds.value = event.api.getSelectedRows().map((r: ServiceAccountDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    (col) => col.field('AccountName').header('Account name', 'admin.serviceAccounts.accountName').width(220).pinned('left').cellClass('account-name-cell'),
    (col) => col.field('Purpose').header('Purpose', 'admin.serviceAccounts.purpose').flex(1),
    (col) => draftStagedColumn(col, t),
    (col) => col.icon('IsActive', { color: '#16a34a', size: 's' })
      .option('valueGetter', (p: any) => p.data?.IsActive ? 'check' : '')
      .option('tooltipValueGetter', () => null)
      .header('Active', 'admin.users.active').width(80),
  ])

async function deleteRows() {
  const id = selectedIds.value[0]
  if (!id) return
  // A row the draft created is simply taken back out of the draft. Deleting a LIVE
  // account stays a live action: it kills every credential the account owns, so the
  // manifest never stages its deletion.
  if (staging.isDraftId(id)) return staging.unstage(staging.draftKeyOf(id))
  if (confirm(t('common.confirmDelete', {}, 'Really delete?'))) {
    await store.deleteEntities(selectedIds.value.filter((x) => !staging.isDraftId(x)))
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
      icon="cpu"
      :title="t('admin.serviceAccounts.title', {}, 'Service Accounts')"
      :description="t('admin.serviceAccounts.emptyHint', {}, 'A service account is a non-human identity for machine-to-machine access — the required link for client_credentials OAuth clients. Create one to let a backend authenticate.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteRows" />
      <CoarMenuDivider v-if="exportMenuVisible" />
      <CoarMenuItem v-if="exportMenuVisible" :label="exportMenuLabel" icon="list-checks"
        @clicked="exportMenuToggle" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
:deep(.account-name-cell) {
  font-weight: 600;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
</style>
