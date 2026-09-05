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
import { useApplicationsStore } from '@/stores/applications.store'
import { roleManifestKey } from '@/stores/realmDraft.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { useClone, buildClonePrefill, ROLE_CLONE } from '@/composables/useClone'
import { draftRowId, useDraftListOverlay, useDraftStaging, type DraftRow } from '@/composables/useDraftStaging'
import { useExportSelectionMenu } from '@/composables/useExportSelectionMenu'
import type { RoleDto } from '@/models/role'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const { stage } = useClone()
const roleStore = useRoleStore()
const appCtx = useAppContextStore()
const applicationsStore = useApplicationsStore()

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
const liveRoles = computed(() =>
  roleStore.roles.filter((r) =>
    appCtx.matchesSingleAppId(r.IsRealmAdmin ? null : r.AppId)))

// ADR-0005: draft-merged roster — the staged key resolves Key ?? app/name (role
// names are unique per App only), so a staged rename still overlays its live row.
const staging = useDraftStaging('roles')
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const liveKey = (row: RoleDto) => roleManifestKey(
  row.IsRealmAdmin ? null : applicationsStore.apps.find((a) => a.Id === row.AppId)?.Slug, row.Name)
const roles = useDraftListOverlay<RoleDto>({
  section: 'roles',
  rows: liveRoles,
  liveKey,
  matchLive: (row, e) => liveKey(row) === (str(e.Key) || roleManifestKey(str(e.App) || null, str(e.Name))),
  overlay: (row, e) => ({
    ...row,
    Name: str(e.Name) || row.Name,
    Description: str(e.Description) || row.Description,
    IsRealmAdmin: e.IsRealmAdmin === true,
  }),
  synthesize: (key, e) => ({
    Id: draftRowId(key),
    Name: str(e.Name) || key,
    Description: str(e.Description) || null,
    IsRealmAdmin: e.IsRealmAdmin === true,
    AppId: applicationsStore.apps.find((a) => a.Slug === str(e.App))?.Id ?? null,
    // Pseudo-ids: the grid only shows the count.
    PermissionIds: (Array.isArray(e.Permissions) ? e.Permissions : []).map((_, i) => String(i)),
  } as unknown as RoleDto),
})

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])
const selectedDeleteStaged = ref(false)

const showEmpty = computed(() => roleStore.loaded && roles.value.length === 0)

const { exportMenuVisible, exportMenuLabel, exportMenuToggle } = useExportSelectionMenu('roles',
  computed(() => {
    const row = roles.value.find((r) => r.Id === selectedIds.value[0])
    if (!row || row.DraftStaged === 'create') return null
    return liveKey(row)
  }))

const builder = applyListGridDefaults(CoarGridBuilder.create<DraftRow<RoleDto>>(), { openable: true })
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
    const selected = event.api.getSelectedRows() as DraftRow<RoleDto>[]
    selectedIds.value = selected.map((r) => r.Id)
    selectedDeleteStaged.value = selected.some((r) => r.DraftStaged === 'delete')
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
  // ADR-0005 staged deletes — realm-admin roles are lockout-protected: the
  // plan would flag the staged deletion as an error, so refuse it upfront.
  if (staging.stagingActive.value) {
    if (staging.isDraftId(id)) return staging.unstage(staging.draftKeyOf(id))
    const row = roles.value.find((r) => r.Id === id)
    if (!row) return
    if (row.IsRealmAdmin) {
      alert(t('admin.roles.cannotDeleteRealmAdmin', {}, 'Realm-admin roles cannot be deleted (lockout protection).'))
      return
    }
    if (row.DraftStaged === 'delete') return staging.unstageDelete(liveKey(row))
    // No confirm: staged deletes are reversible; the apply popconfirm gates.
    return staging.stageDelete(liveKey(row))
  }
  if (confirm(t('common.confirmDelete', {}, 'Really delete?'))) {
    await roleStore.deleteRole(id)
  }
}

// Clone: roles load in full on the list, so prefill straight from the store —
// blank the Name; the App-link + catalog subset clone 1:1.
function cloneSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const source = roleStore.roles.find((r) => r.Id === id)
  if (!source) return
  stage(ROLE_CLONE.entity, buildClonePrefill(source, ROLE_CLONE.descriptor))
  navigateToModal('create')
}

// Apps supply the slug half of every role key (`app/name`).
onMounted(() => Promise.all([roleStore.initialize(), applicationsStore.initialize()]))
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

    <!-- Viewport context menu (empty area) -->
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
