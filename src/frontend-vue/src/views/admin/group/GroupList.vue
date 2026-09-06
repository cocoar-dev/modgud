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
import { useDraftListOverlay, useDraftStaging, type DraftRow } from '@/composables/useDraftStaging'
import { useExportSelectionMenu } from '@/composables/useExportSelectionMenu'
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

const liveGroups = computed<GroupListRow[]>(() =>
  groupStore.groups.filter((g) =>
    appCtx.matchesBoundToSlugs(g.BoundTo, selectedAppSlug.value))
    .map((g) => ({ ...g, HasPermissions: g.RoleIds.length > 0 })))

// ADR-0017: draft-merged roster (natural key = the group name).
const staging = useDraftStaging('groups')
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const arr = (v: unknown) => (Array.isArray(v) ? (v as string[]) : [])
const groups = useDraftListOverlay<GroupListRow>({
  section: 'groups',
  rows: liveGroups,
  liveKey: (row) => row.Name,
  matchLive: (row, e) => row.Name === str(e.Name),
  overlay: (row, e) => ({
    ...row,
    // The staged entity carries the group's id, so a changed Name is a RENAME of
    // this very row — show it, don't keep the live name.
    Name: str(e.Name) || row.Name,
    Description: str(e.Description) || row.Description,
    MembershipMode: (str(e.MembershipMode) || row.MembershipMode) as GroupDto['MembershipMode'],
    // Pseudo-ids: the grid only shows the count (manifest members are user keys).
    MemberIds: str(e.MembershipMode) === 'Auto' ? row.MemberIds : arr(e.Members),
    HasPermissions: arr(e.Roles).length > 0,
  }),
  synthesize: (key, e) => ({
    Id: `draft__${key}`,
    Name: str(e.Name) || key,
    Description: str(e.Description) || null,
    MembershipMode: (str(e.MembershipMode) || 'Manual') as GroupDto['MembershipMode'],
    MembershipLastError: null,
    MemberIds: arr(e.Members),
    RoleIds: [],
    BoundTo: arr(e.BoundTo),
    HasPermissions: arr(e.Roles).length > 0,
  } as unknown as GroupListRow),
})

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])
const selectedDeleteStaged = ref(false)

const showEmpty = computed(() => groupStore.loaded && groups.value.length === 0)

const { exportMenuVisible, exportMenuLabel, exportMenuToggle } = useExportSelectionMenu('groups',
  computed(() => {
    const row = groups.value.find((g) => g.Id === selectedIds.value[0])
    if (!row || row.DraftStaged === 'create') return null
    return row.Name
  }))

const builder = applyListGridDefaults(CoarGridBuilder.create<DraftRow<GroupListRow>>(), { openable: true })
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
    const selected = event.api.getSelectedRows() as DraftRow<GroupListRow>[]
    selectedIds.value = selected.map((r) => r.Id)
    selectedDeleteStaged.value = selected.some((r) => r.DraftStaged === 'delete')
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    (col) => col.field('Name').header('Name', 'admin.groups.name').flex(2).minWidth(180),
    (col) => col.field('Description').header('Description', 'admin.groups.description').flex(1),
    (col) => col.field('DraftStaged').header('Draft', 'admin.realmConfig.gridCol')
      .valueGetter((p: any) => p.data?.DraftStaged === 'create'
        ? t('admin.realmConfig.gridTag.create', {}, 'Staged (new)')
        : p.data?.DraftStaged === 'update'
          ? t('admin.realmConfig.gridTag.update', {}, 'Staged')
          : p.data?.DraftStaged === 'delete'
            ? t('admin.realmConfig.gridTag.delete', {}, 'Staged (delete)')
            : '')
      .width(120)
      .classRule('draft-staged-cell', (p: any) => !!p.data?.DraftStaged && p.data.DraftStaged !== 'delete')
      .classRule('draft-staged-cell-delete', (p: any) => p.data?.DraftStaged === 'delete'),
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
  // ADR-0017 staged deletes (admin-conferring groups are apply-guarded; the
  // plan flags a staged deletion of one as an error).
  if (staging.stagingActive.value) {
    if (staging.isDraftId(id)) return staging.unstage(staging.draftKeyOf(id))
    const row = groups.value.find((r) => r.Id === id)
    if (!row) return
    if (row.DraftStaged === 'delete') return staging.unstageDelete(row.Name)
    // No confirm: staged deletes are reversible; the apply popconfirm gates.
    return staging.stageDelete(row.Name)
  }
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
      <CoarMenuItem
        :label="selectedDeleteStaged
          ? t('admin.realmConfig.undelete', {}, 'Undo delete')
          : t('common.delete', {}, 'Delete')"
        :icon="selectedDeleteStaged ? 'undo-2' : 'trash-2'"
        @clicked="deleteSelected" />
      <CoarMenuDivider v-if="exportMenuVisible" />
      <CoarMenuItem v-if="exportMenuVisible" :label="exportMenuLabel" icon="list-checks"
        @clicked="exportMenuToggle" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
:deep(.draft-staged-cell) {
  color: var(--coar-text-semantic-info, #2563eb);
  font-weight: 600;
}

:deep(.draft-staged-cell-delete) {
  color: var(--coar-text-semantic-error, #dc2626);
  font-weight: 600;
}
</style>
