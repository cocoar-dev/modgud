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
import { useRoleStore } from '@/stores/role.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import type { RoleDto } from '@/models/role'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const roleStore = useRoleStore()
const appCtx = useAppContextStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.roles.title', {}, 'Roles')
  ctx.header.icon = 'shield'
  ctx.content.container = false
}), { immediate: true })

// Roles with IsRealmAdmin=true (e.g. System Admin) are kept in the
// 'global' bucket alongside roles that have no AppId — both are
// realm-scoped from the admin's perspective.
const roles = computed(() =>
  roleStore.roles.filter((r) =>
    appCtx.matchesSingleAppId(r.IsRealmAdmin ? null : r.AppId)))

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => roleStore.loaded && roleStore.roles.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<RoleDto>(), { openable: true })
  .persistColumnState('admin-roles')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(roles)
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
    selectedIds.value = event.api.getSelectedRows().map((r: RoleDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    (col) => col.field('Name').header('Name', 'admin.roles.name').flex(2).minWidth(180),
    (col) => col.field('IsRealmAdmin').header('Realm Admin', 'admin.roles.isRealmAdmin').width(120)
      .option('valueGetter', (p: any) => p.data?.IsRealmAdmin ? '✓' : ''),
    (col) => col.field('Description').header('Description', 'admin.roles.description').flex(1),
    (col) => col.field('PermissionIds').header('Grants', 'admin.roles.permissions').flex(2)
      .option('valueGetter', (p: any) => {
        const r = p.data
        if (!r) return ''
        if (r.IsRealmAdmin) return 'realm:admin'
        return `${(r.PermissionIds || []).length} permission(s)`
      }),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (confirm(t('common.confirmDelete', {}, 'Really delete?'))) {
    await roleStore.deleteRole(id)
  }
}

onMounted(() => roleStore.initialize())
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
      icon="shield"
      :title="t('admin.roles.title', {}, 'Roles')"
      :description="t('admin.roles.emptyHint', {}, 'A role bundles permissions into a job function you can grant to users or groups. Create the first role to define what people may do.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <!-- Row context menu -->
    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteSelected" />
    </CoarContextMenu>

    <!-- Viewport context menu (empty area) -->
    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
