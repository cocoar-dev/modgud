<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
  CoarMenuDivider,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import { useUI } from '@/composables/useUI'
import type { LoginProviderDto } from '@/models/loginProvider'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useLoginProviderStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.loginProviders.title', {}, 'Login-Provider')
  ctx.header.icon = 'log-in'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() => store.providers)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const builder = CoarGridBuilder.create<LoginProviderDto>()
  .persistColumnState('admin-login-providers')
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
    selectedIds.value = event.api.getSelectedRows().map((r: LoginProviderDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.field('Name').header('Name', 'common.name').flex(1).minWidth(160),
    (col) => col.field('DisplayName').header('Display Name', 'admin.loginProviders.displayName').flex(1),
    (col) => col.field('Type').header('Type', 'admin.loginProviders.type').width(160),
    (col) => col.field('Description').header('Description', 'common.description').flex(2),
    (col) => col.field('IsBuiltIn').header('Built-in', 'admin.loginProviders.builtIn').width(100)
      .option('valueGetter', (p: any) => p.data?.IsBuiltIn
        ? t('common.yes', {}, 'Ja')
        : t('common.no', {}, 'Nein')),
  ])

const selectedProvider = computed(() => rows.value.find((p) => p.Id === selectedIds.value[0]))

async function deleteSelected() {
  const provider = selectedProvider.value
  if (!provider) return
  if (provider.IsBuiltIn) {
    alert(t('admin.loginProviders.builtInDelete', {}, 'Built-in Provider können nicht gelöscht werden.'))
    return
  }
  if (!confirm(t('common.confirmDelete', {}, 'Wirklich löschen?'))) return
  try { await store.remove(provider.Id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

onMounted(() => store.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid :builder="builder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">
          {{ t('common.create', {}, 'Erstellen') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Öffnen')" icon="pencil"
        @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Erstellen')" icon="plus"
        @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Löschen')" icon="trash-2"
        :disabled="!selectedProvider || selectedProvider.IsBuiltIn"
        @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Erstellen')" icon="plus"
        @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
