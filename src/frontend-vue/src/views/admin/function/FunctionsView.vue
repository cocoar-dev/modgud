<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton, useContextMenu, CoarContextMenu, CoarMenuItem, CoarMenuDivider } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useFunctionStore } from '@/stores/function.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import type { FunctionPrincipalDto } from '@/models/function'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useFunctionStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.functions.title', {}, 'Functions')
  ctx.header.icon = 'briefcase'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() => store.entities)

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => store.allLoaded && rows.value.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<FunctionPrincipalDto>(), { openable: true })
  .persistColumnState('admin-functions')
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
    selectedIds.value = event.api.getSelectedRows().map((r: FunctionPrincipalDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    (col) => col.field('AccountName').header('Account name', 'admin.functions.accountName').width(220).pinned('left').cellClass('account-name-cell'),
    (col) => col.field('Purpose').header('Purpose', 'admin.functions.purpose').flex(1),
    (col) => col.icon('TerminalPolicy', { color: '#0284c7', size: 's' })
      .option('valueGetter', (p: any) => p.data?.TerminalPolicy?.Enabled ? 'monitor-smartphone' : '')
      .option('tooltipValueGetter', () => null)
      .header('Terminals', 'admin.functions.terminalsEnabled').width(100),
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
      :title="t('admin.functions.title', {}, 'Functions')"
      :description="t('admin.functions.emptyHint', {}, 'A function is the business identity of a staffed role — like a gate porter for one customer — filled by changing people on shared terminals. Create one to give that role its own permissions and audit identity.')"
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
:deep(.account-name-cell) {
  font-weight: 600;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}
</style>
