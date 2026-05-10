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
import { useOAuthApiStore } from '@/stores/oauthApi.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useUI } from '@/composables/useUI'
import type { OAuthApiDto } from '@/models/oauth'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useOAuthApiStore()
const appCtx = useAppContextStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.oauthApis.title', {}, 'OAuth-APIs')
  ctx.header.icon = 'server'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() =>
  store.apis.filter((a) => appCtx.matchesSingleAppId(a.AppId)))
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const builder = CoarGridBuilder.create<OAuthApiDto>()
  .persistColumnState('admin-oauth-apis')
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
    selectedIds.value = event.api.getSelectedRows().map((r: OAuthApiDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.field('Name').header('Name', 'admin.oauthApis.name').flex(1).minWidth(140),
    (col) => col.field('DisplayName').header('Display Name', 'admin.oauthApis.displayName').flex(1),
    (col) => col.field('Description').header('Description', 'common.description').flex(2),
    (col) => col.field('Scopes').header('Scopes', 'admin.oauthApis.scopes').width(110)
      .option('valueGetter', (p: any) => (p.data?.Scopes ?? []).length),
    (col) => col.field('Secrets').header('Secrets', 'admin.oauthApis.secretCount').width(110)
      .option('valueGetter', (p: any) => (p.data?.Secrets ?? []).length),
    (col) => col.field('Enabled').header('Enabled', 'common.enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled
        ? t('common.yes', {}, 'Ja')
        : t('common.no', {}, 'Nein')),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (!confirm(t('common.confirmDelete', {}, 'Wirklich löschen?'))) return
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
      <CoarMenuItem :label="t('common.delete', {}, 'Löschen')" icon="trash-2" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Erstellen')" icon="plus"
        @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
