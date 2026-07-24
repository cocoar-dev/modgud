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
import { useGridLocale } from '@/composables/useGridLocale'
import { useClone, buildClonePrefill, APP_CLONE } from '@/composables/useClone'
import type { ApplicationDto } from '@/models/application'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const { stage } = useClone()
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

const showEmpty = computed(() => store.loaded && store.apps.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<ApplicationDto>(), { openable: true })
  .persistColumnState('admin-apps')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  // System apps (modgud, control-plane) are bootstrap-managed and
  // should never be edited from the admin surface — dim the row to
  // signal "look but don't touch".
  .rowClassRules({
    'is-system': (p: any) => p.data?.IsSystem === true,
  })
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
    (col) => col
      .wrap(col.field('Slug').header('Slug', 'admin.apps.slug').width(180))
      .left({
        icon: (r: any) => r?.IsSystem ? 'lock' : null,
        color: 'var(--coar-text-neutral-secondary, #9ca3af)',
        tooltip: t('admin.system.lockedHint', {}, 'System entry — managed by the IdP, read-only'),
      }),
    (col) => col.field('DisplayName').header('Display Name', 'admin.apps.displayName').flex(1).minWidth(180),
    (col) => col.field('Description').header('Description', 'common.description').flex(2),
    (col) => col.field('Permissions').header('Permissions', 'admin.apps.permissions').flex(2)
      .option('valueGetter', (p: any) => (p.data?.Permissions ?? [])
        .map((perm: any) => `${perm.Resource}:${perm.Action}`)
        .join(', ')),
    (col) => col.icon('IsSystem', { size: 's' })
      .option('valueGetter', (p: any) => p.data?.IsSystem ? 'lock' : '')
      .option('tooltipValueGetter', () => null)
      .header('System', 'admin.apps.isSystem').width(90),
  ])

// Clone: load the full source App (the list endpoint omits Settings + only the
// detail DTO carries the complete catalog), build a prefill with a blank slug +
// fresh catalog ids, then open the Create modal pre-filled.
async function cloneSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  try {
    const source = await store.loadOne(id)
    if (!source) return
    stage(APP_CLONE.entity, buildClonePrefill(source, APP_CLONE.descriptor))
    navigateToModal('create')
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
}

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (selectedIsSystem.value) {
    alert(t('admin.apps.cannotDeleteSystem', {}, 'The system app can\'t be deleted.'))
    return
  }
  if (!confirm(t('admin.apps.confirmDelete', {}, 'Really delete this app?'))) return
  try { await store.remove(id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

onMounted(() => store.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">
          {{ t('common.create', {}, 'Create') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="layout-grid"
      :title="t('admin.apps.title', {}, 'Applications')"
      :description="t('admin.apps.emptyHint', {}, 'An application groups the resources, roles and OAuth clients that belong to one product, so permissions can be scoped per app.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil"
        @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus"
        @clicked="navigateToModal('create')" />
      <CoarMenuItem :label="t('common.clone', {}, 'Clone')" icon="copy"
        @clicked="cloneSelected" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2"
        :disabled="selectedIsSystem" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus"
        @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
