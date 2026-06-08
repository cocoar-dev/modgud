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
import { useRealmStore } from '@/stores/realm.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import type { RealmDto } from '@/models/realm'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useRealmStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.realms.title', {}, 'Realms')
  ctx.header.icon = 'globe'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() => store.realms)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedSlugs = ref<string[]>([])

const showEmpty = computed(() => store.loaded && store.realms.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<RealmDto>(), { openable: true })
  .persistColumnState('admin-realms')
  .option('getRowId', (p: any) => p.data.Slug)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event) => {
    if (event.data) navigateToModal(event.data.Slug)
  })
  .onCellContextMenu((event) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selectedSlugs.value = event.api.getSelectedRows().map((r: RealmDto) => r.Slug)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.field('Slug').header('Slug', 'admin.realms.slug').width(160),
    (col) => col.field('DisplayName').header('Display Name', 'admin.realms.displayName').flex(1).minWidth(180),
    (col) => col.field('Description').header('Description', 'common.description').flex(2),
    (col) => col.field('Domains').header('Domains', 'admin.realms.domains').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Domains ?? []).join(', ')),
    (col) => col.field('IsActive').header('Active', 'common.active').width(90)
      .option('valueGetter', (p: any) => p.data?.IsActive
        ? t('common.yes', {}, 'Ja')
        : t('common.no', {}, 'Nein')),
    (col) => col.field('IsControlPlane').header('Control Plane', 'admin.realms.isControlPlane').width(150)
      .option('valueGetter', (p: any) => p.data?.IsControlPlane
        ? t('admin.realms.controlPlaneBadge', {}, 'Control Plane')
        : ''),
  ])

async function deleteSelected() {
  const slug = selectedSlugs.value[0]
  if (!slug) return
  if (!confirm(t('admin.realms.confirmDelete', { slug }, `Realm "${slug}" wirklich löschen?`))) return
  try { await store.remove(slug) } catch (e: any) { alert(e?.message ?? String(e)) }
}

onMounted(() => store.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">
          {{ t('common.create', {}, 'Erstellen') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="globe"
      :title="t('admin.realms.title', {}, 'Realms')"
      :description="t('admin.realms.emptyHint', {}, 'A realm is an isolated tenant with its own users, clients and database. Create one to host a separate organisation or environment.')"
      :cta-label="t('common.create', {}, 'Erstellen')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Öffnen')" icon="pencil"
        @clicked="selectedSlugs[0] && navigateToModal(selectedSlugs[0])" />
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
