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
import { useGroupStore } from '@/stores/group.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { useClone, buildClonePrefill, GROUP_CLONE } from '@/composables/useClone'
import type { GroupDto } from '@/models/group'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const { stage } = useClone()
const groupStore = useGroupStore()
const appCtx = useAppContextStore()
const appsStore = useApplicationsStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.groups.title', {}, 'Groups')
  ctx.header.icon = 'users'
  ctx.content.container = false
}), { immediate: true })

// Groups carry their App-link as a slug-list in BoundTo (with '*' as
// the realm-wide wildcard). Translate the selected App.Id back to a
// slug for the comparison.
const selectedAppSlug = computed(() => {
  const id = appCtx.selectedAppId
  if (!id) return null
  return appsStore.apps.find((a) => a.Id === id)?.Slug ?? null
})
type GroupListRow = GroupDto & { HasPermissions: boolean }

const groups = computed<GroupListRow[]>(() =>
  groupStore.groups.filter((g) =>
    appCtx.matchesBoundToSlugs(g.BoundTo, selectedAppSlug.value))
    .map((g) => ({ ...g, HasPermissions: g.RoleIds.length > 0 })))

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => groupStore.loaded && groupStore.groups.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<GroupListRow>(), { openable: true })
  .persistColumnState('admin-groups')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(groups)
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
    selectedIds.value = event.api.getSelectedRows().map((r: GroupListRow) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    (col) => col.field('Name').header('Name', 'admin.groups.name').flex(2).minWidth(180),
    (col) => col.field('Description').header('Description', 'admin.groups.description').flex(1),
    (col) => col.tag('MembershipMode', {
      variantMap: { Manual: 'neutral', Auto: 'info', Error: 'error' },
      i18nPrefix: 'admin.groups.membership.',
    }).header('Type', 'admin.groups.membershipMode').width(140)
      .option('valueGetter', (p: any) => p.data?.MembershipLastError ? 'Error' : p.data?.MembershipMode),
    (col) => col.field('MemberIds').header('Members', 'admin.groups.members').width(120)
      .option('valueGetter', (p: any) => (p.data?.MemberIds || []).length),
    (col) => col.tag('HasPermissions', {
      variantMap: { true: 'info', false: 'neutral' },
      i18nPrefix: 'admin.groups.permissionsTag.',
    }).header('Permissions', 'admin.groups.permissions').width(180),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (confirm(t('common.confirmDelete', {}, 'Really delete?'))) {
    await groupStore.deleteGroup(id)
  }
}

// Clone: groups load in full on the list, so prefill straight from the store —
// blank the Name; members, roles, script, BoundTo clone 1:1.
function cloneSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const source = groupStore.groups.find((g) => g.Id === id)
  if (!source) return
  stage(GROUP_CLONE.entity, buildClonePrefill(source, GROUP_CLONE.descriptor))
  navigateToModal('create')
}

onMounted(() => groupStore.initialize())
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
      icon="users"
      :title="t('admin.groups.title', {}, 'Groups')"
      :description="t('admin.groups.emptyHint', {}, 'Groups bundle users so roles and permissions can be granted to many people at once — manually or via an auto-membership script.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
      <CoarMenuItem :label="t('common.clone', {}, 'Clone')" icon="copy" @clicked="cloneSelected" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
