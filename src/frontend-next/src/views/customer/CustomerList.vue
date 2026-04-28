<script setup lang="ts">
import { onMounted, computed, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  CoarCheckbox,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
  CoarMenuDivider,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useCustomerStore } from '@/stores/customer.store'
import { useUI } from '@/composables/useUI'
import type { CustomerDto } from '@/models/customer'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const customerStore = useCustomerStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.customers', {}, 'Customers')
  ctx.header.subTitle = undefined
  ctx.header.icon = 'building-2'
  ctx.content.container = true
}), { immediate: true })

const showArchived = ref(false)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const contextCustomer = ref<CustomerDto | undefined>()

const rowData = computed(() =>
  showArchived.value ? customerStore.allCustomersSorted : customerStore.activeCustomers,
)

const builder = CoarGridBuilder.create<CustomerDto>()
  .persistColumnState('admin-customers')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rowData)
  .searchHighlight()
  .rowSelection('single')
  .rowClassRules({
    'opacity-50': (p) => !!p.data?.IsArchived,
  })
  .onCellDoubleClicked((event) => {
    if (event.data) navigateToModal(event.data.Id)
  })
  .onCellContextMenu((event) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    contextCustomer.value = event.data
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    (col) => col.icon('Important', { color: '#3b82f6', size: 's' })
      .option('valueGetter', (p: any) => p.data?.Important ? 'check' : '')
      .header('Important', 'admin.customers.important').width(100),
    (col) => col.field('Name').header('Name', 'admin.customers.name').flex(1),
  ])

async function archiveCustomer() {
  if (contextCustomer.value) {
    await customerStore.archive([contextCustomer.value.Id])
  }
}

async function restoreCustomer() {
  if (contextCustomer.value) {
    await customerStore.restore([contextCustomer.value.Id])
  }
}

onMounted(() => customerStore.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid :builder="builder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarCheckbox v-model="showArchived" :label="t('admin.customers.showArchived', {}, 'show archived customers')" />
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">{{ t('common.create', {}, 'Create') }}</CoarButton>
      </template>
    </CoarDataGrid>

    <!-- Row context menu -->
    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="contextCustomer && navigateToModal(contextCustomer.Id)" />
      <CoarMenuItem :label="t('admin.customers.newCustomer', {}, 'New Customer')" icon="plus" @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem
        v-if="contextCustomer && !contextCustomer.IsArchived"
        :label="t('common.archive', {}, 'Archive')"
        icon="box-archive"
        @clicked="archiveCustomer"
      />
      <CoarMenuItem
        v-if="contextCustomer?.IsArchived"
        :label="t('common.restore', {}, 'Restore')"
        icon="undo-2"
        @clicked="restoreCustomer"
      />
    </CoarContextMenu>

    <!-- Viewport context menu (empty area) -->
    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('admin.customers.newCustomer', {}, 'New Customer')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
