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
import { useOAuthClientStore } from '@/stores/oauthClient.store'
import { useUI } from '@/composables/useUI'
import type { OAuthClientDto } from '@/models/oauth'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useOAuthClientStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.oauthClients.title', {}, 'OAuth-Clients')
  ctx.header.icon = 'app-window'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() => store.clients)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const builder = CoarGridBuilder.create<OAuthClientDto>()
  .persistColumnState('admin-oauth-clients')
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
    selectedIds.value = event.api.getSelectedRows().map((r: OAuthClientDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.field('ClientId').header('Client ID', 'admin.oauthClients.clientId').flex(1).minWidth(180),
    (col) => col.field('DisplayName').header('Display Name', 'admin.oauthClients.displayName').flex(1),
    (col) => col.field('ClientType').header('Type', 'admin.oauthClients.type').width(120),
    (col) => col.field('Enabled').header('Enabled', 'admin.oauthClients.enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled === false
        ? t('common.no', {}, 'No')
        : t('common.yes', {}, 'Yes')),
    (col) => col.field('RedirectUris').header('Redirects', 'admin.oauthClients.redirectCount').width(110)
      .option('valueGetter', (p: any) => (p.data?.RedirectUris ?? []).length),
    (col) => col.field('AllowedGrantTypes').header('Grants', 'admin.oauthClients.grantCount').width(110)
      .option('valueGetter', (p: any) => (p.data?.AllowedGrantTypes ?? []).length),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (!confirm(t('common.confirmDelete', {}, 'Wirklich löschen?'))) return
  try {
    await store.remove(id)
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
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
