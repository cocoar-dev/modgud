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
import { useDraftListOverlay, type DraftRow } from '@/composables/useDraftStaging'
import { useAppContextStore } from '@/stores/appContext.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { useClone, buildClonePrefill, API_CLONE } from '@/composables/useClone'
import type { OAuthApiDto } from '@/models/oauth'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const { stage } = useClone()
const store = useOAuthApiStore()
const appCtx = useAppContextStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.oauthApis.title', {}, 'OAuth-APIs')
  ctx.header.icon = 'server'
  ctx.content.container = false
}), { immediate: true })

const liveRows = computed(() =>
  store.apis.filter((a) => appCtx.matchesSingleAppId(a.AppId)))

// ADR-0005: draft-merged roster (natural key = the immutable audience/Name).
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const arr = (v: unknown) => (Array.isArray(v) ? (v as string[]) : [])
const rows = useDraftListOverlay<OAuthApiDto>({
  section: 'apis',
  rows: liveRows,
  matchLive: (row, e) => row.Name === str(e.Name),
  overlay: (row, e) => ({
    ...row,
    DisplayName: str(e.DisplayName) || row.DisplayName,
    Description: str(e.Description) || row.Description,
    Scopes: arr(e.Scopes).length ? arr(e.Scopes) : row.Scopes,
    Enabled: typeof e.Enabled === 'boolean' ? e.Enabled : row.Enabled,
  }),
  synthesize: (key, e): OAuthApiDto => ({
    Id: `draft__${key}`,
    Name: str(e.Name) || key,
    DisplayName: str(e.DisplayName) || null,
    Description: str(e.Description) || null,
    Enabled: e.Enabled !== false,
    Scopes: arr(e.Scopes),
    UserClaims: arr(e.UserClaims),
    AppId: null,
    PermissionIds: [],
    AllowDynamicRegistration: e.AllowDynamicRegistration === true,
    HasImplicitScope: false,
  } as OAuthApiDto),
})
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => store.loaded && rows.value.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<DraftRow<OAuthApiDto>>(), { openable: true })
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
    (col) => col.field('Name').header('Audience', 'admin.oauthApis.audience').flex(2).minWidth(160),
    (col) => col.field('DisplayName').header('Display Name', 'admin.oauthApis.displayName').flex(1),
    (col) => col.field('Description').header('Description', 'common.description').flex(2),
    (col) => col.field('Scopes').header('Scopes', 'admin.oauthApis.scopes').width(110)
      .option('valueGetter', (p: any) => (p.data?.Scopes ?? []).length),
    (col) => col.field('DraftStaged').header('Draft', 'admin.realmConfig.gridCol')
      .valueGetter((p: any) => p.data?.DraftStaged === 'create'
        ? t('admin.realmConfig.gridTag.create', {}, 'Staged (new)')
        : p.data?.DraftStaged === 'update'
          ? t('admin.realmConfig.gridTag.update', {}, 'Staged')
          : '')
      .width(120)
      .classRule('draft-staged-cell', (p: any) => !!p.data?.DraftStaged),
    (col) => col.tag('Enabled', {
      variantMap: { active: 'success', inactive: 'neutral' },
      i18nPrefix: 'common.statusTag.',
    })
      .header('Enabled', 'common.enabled').width(110)
      .option('valueGetter', (p: any) => p.data?.Enabled ? 'active' : 'inactive'),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (!confirm(t('admin.oauthApis.confirmDelete', {}, 'Really delete this API?'))) return
  try { await store.remove(id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

// Clone: load the full API, blank the immutable Name (the aud), open Create
// pre-filled. The linked App + its catalog subset clone 1:1; secrets reset.
async function cloneSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  try {
    const source = await store.loadOne(id)
    if (!source) return
    stage(API_CLONE.entity, buildClonePrefill(source, API_CLONE.descriptor))
    navigateToModal('create')
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
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
      icon="server"
      :title="t('admin.oauthApis.title', {}, 'OAuth-APIs')"
      :description="t('admin.oauthApis.emptyHint', {}, 'An API is a protected backend resource clients request tokens for. It owns the scopes a client may ask for to reach your services. Define your first API here.')"
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
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus"
        @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
:deep(.draft-staged-cell) {
  color: var(--coar-text-semantic-info, #2563eb);
  font-weight: 600;
}
</style>
