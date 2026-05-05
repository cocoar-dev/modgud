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
import { useApplicationsStore } from '@/stores/applications.store'
import { useUI } from '@/composables/useUI'
import type { ApplicationDto } from '@/models/application'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useApplicationsStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.apps.title', {}, 'Applications')
  ctx.header.icon = 'layout-grid'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() => store.apps)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])
const selectedIsSystem = ref(false)

const builder = CoarGridBuilder.create<ApplicationDto>()
  .persistColumnState('admin-apps')
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
    const sel = event.api.getSelectedRows() as ApplicationDto[]
    selectedIds.value = sel.map((a) => a.Id)
    selectedIsSystem.value = sel.some((a) => a.IsSystem)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.field('Slug').header('Slug', 'admin.apps.slug').width(180),
    (col) => col.field('DisplayName').header('Display Name', 'admin.apps.displayName').flex(1).minWidth(180),
    (col) => col.field('Description').header('Description', 'common.description').flex(2),
    (col) => col.field('Resources').header('Resources', 'admin.apps.resources').flex(2)
      .option('valueGetter', (p: any) => (p.data?.Resources ?? []).join(', ')),
    (col) => col.field('IsSystem').header('System', 'admin.apps.isSystem').width(100)
      .option('valueGetter', (p: any) => p.data?.IsSystem
        ? t('common.yes', {}, 'Ja')
        : t('common.no', {}, 'Nein')),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (selectedIsSystem.value) {
    alert(t('admin.apps.cannotDeleteSystem', {}, 'Die System-App kann nicht gelöscht werden.'))
    return
  }
  if (!confirm(t('admin.apps.confirmDelete', {}, 'App wirklich löschen?'))) return
  try { await store.remove(id) } catch (e: any) { alert(e?.message ?? String(e)) }
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
        :disabled="selectedIsSystem" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Erstellen')" icon="plus"
        @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
