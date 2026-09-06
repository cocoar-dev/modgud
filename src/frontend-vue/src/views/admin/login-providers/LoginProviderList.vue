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
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import { useDraftListOverlay, useDraftStaging, type DraftRow } from '@/composables/useDraftStaging'
import { useExportSelectionMenu } from '@/composables/useExportSelectionMenu'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import type { LoginProviderDto, LoginProviderType } from '@/models/loginProvider'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useLoginProviderStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.loginProviders.title', {}, 'Login-Provider')
  ctx.header.icon = 'log-in'
  ctx.content.container = false
}), { immediate: true })

const liveRows = computed(() => store.providers)

// ADR-0017: draft-merged roster (natural key = the provider slug).
const staging = useDraftStaging('loginProviders')
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const rows = useDraftListOverlay<LoginProviderDto>({
  section: 'loginProviders',
  rows: liveRows,
  liveKey: (row) => row.Slug,
  matchLive: (row, e) => row.Slug === str(e.Slug),
  overlay: (row, e) => ({
    ...row,
    DisplayName: str(e.DisplayName) || row.DisplayName,
    Enabled: e.Enabled === true,
    ClientId: str(e.ClientId) || row.ClientId,
  }),
  synthesize: (key, e) => ({
    Id: `draft__${key}`,
    Slug: str(e.Slug) || key,
    DisplayName: str(e.DisplayName) || key,
    Type: (str(e.Type) || 'Oidc') as LoginProviderType,
    Flavor: str(e.Flavor),
    Enabled: e.Enabled === true,
    ClientId: str(e.ClientId),
    HasClientSecret: false,
    IsBuiltIn: false,
    IconName: str(e.IconName) || null,
  } as unknown as LoginProviderDto),
})

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])
const selectedDeleteStaged = ref(false)

// i18n helper for the Type column.
function typeLabel(type: LoginProviderType): string {
  switch (type) {
    case 'Internal': return t('admin.loginProviders.type.values.internal', {}, 'Intern')
    case 'Oidc': return t('admin.loginProviders.type.values.oidc', {}, 'OIDC')
    case 'Saml': return t('admin.loginProviders.type.values.saml', {}, 'SAML')
    case 'Ldap': return t('admin.loginProviders.type.values.ldap', {}, 'LDAP')
    case 'Kerberos': return t('admin.loginProviders.type.values.kerberos', {}, 'Kerberos')
    default: return type
  }
}

const showEmpty = computed(() => store.loaded && rows.value.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<DraftRow<LoginProviderDto>>(), { openable: true })
  .persistColumnState('admin-login-providers')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  // Built-in providers (Internal Authentication) ship with the IdP and
  // can't be removed/edited — dim the row to telegraph that.
  .rowClassRules({
    'is-system': (p: any) => p.data?.IsBuiltIn === true,
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
    const selected = event.api.getSelectedRows() as DraftRow<LoginProviderDto>[]
    selectedIds.value = selected.map((r) => r.Id)
    selectedDeleteStaged.value = selected.some((r) => r.DraftStaged === 'delete')
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.icon('IconName', { size: 's' })
      .option('valueGetter', (p: any) => p.data?.IconName ?? (p.data?.Type === 'Internal' ? 'lock' : 'key-round'))
      .option('tooltipValueGetter', () => null)
      .header('').width(48).resizable(false),
    (col) => col.field('DisplayName').header('Name', 'admin.loginProviders.displayName').flex(2),
    (col) => col.field('Type').header('Type', 'admin.loginProviders.type.label').width(120)
      .option('valueGetter', (p: any) => {
        if (!p.data) return ''
        const base = typeLabel(p.data.Type as LoginProviderType)
        return p.data.IsBuiltIn
          ? `${base} · ${t('admin.loginProviders.builtIn.badge', {}, 'System')}`
          : base
      }),
    (col) => col.field('Flavor').header('Flavor', 'admin.loginProviders.flavor').width(140),
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
    (col) => col.tag('Enabled', {
      variantMap: { active: 'success', inactive: 'neutral' },
      i18nPrefix: 'common.statusTag.',
    })
      .header('Active', 'admin.loginProviders.enabled').width(110)
      .option('valueGetter', (p: any) => p.data?.Enabled ? 'active' : 'inactive'),
    (col) => col.field('ClientId').header('Client ID', 'admin.loginProviders.clientId').flex(2),
    (col) => col.field('HasClientSecret').header('Secret', 'admin.loginProviders.hasSecret').width(100)
      .option('valueGetter', (p: any) => p.data?.HasClientSecret ? '••••••' : '—'),
    (col) => col.date('UpdatedAt', { includeTime: true }).header('Updated', 'admin.loginProviders.updatedAt').width(170),
  ])

const selectedProvider = computed(() => rows.value.find((p) => p.Id === selectedIds.value[0]))

const { exportMenuVisible, exportMenuLabel, exportMenuToggle } = useExportSelectionMenu('loginProviders',
  computed(() => {
    const row = selectedProvider.value
    if (!row || row.DraftStaged === 'create' || row.IsBuiltIn) return null
    return row.Slug
  }))

async function toggleEnabled() {
  const provider = selectedProvider.value
  if (!provider) return
  if (provider.IsBuiltIn) {
    alert(t('admin.loginProviders.errors.internalNotEditable', {}, 'The built-in internal login provider can\'t be edited.'))
    return
  }
  try {
    // Inline grid toggle — immediate PATCH of just the Enabled property
    // (this is the "übers Grid" quick path; the edit modal stages it instead).
    await store.setEnabled(provider.Id, !provider.Enabled)
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
}

async function deleteSelected() {
  const provider = selectedProvider.value
  if (!provider) return
  if (provider.IsBuiltIn) {
    alert(t('admin.loginProviders.errors.internalNotEditable', {}, 'The built-in internal login provider can\'t be edited.'))
    return
  }
  // ADR-0017 staged deletes.
  if (staging.stagingActive.value) {
    if (staging.isDraftId(provider.Id)) return staging.unstage(staging.draftKeyOf(provider.Id))
    if ((provider as DraftRow<LoginProviderDto>).DraftStaged === 'delete')
      return staging.unstageDelete(provider.Slug)
    // No confirm: staged deletes are reversible; the apply popconfirm gates.
    return staging.stageDelete(provider.Slug)
  }
  if (!confirm(t('admin.loginProviders.confirmDelete', {}, 'Really delete this login provider?'))) return
  try { await store.remove(provider.Id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

// "Add Provider" routes straight into the unified modal in Add mode (id='create').
// The modal hosts the flavor picker in its header-actions slot, so the admin
// fills in Type/Flavor + all other fields in one place and one Save click.
function openAddDialog() {
  navigateToModal('create')
}

onMounted(() => store.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="openAddDialog">
          {{ t('admin.loginProviders.add', {}, 'Add Provider') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="log-in"
      :title="t('admin.loginProviders.title', {}, 'Login-Provider')"
      :description="t('admin.loginProviders.emptyHint', {}, 'Login providers are the ways users can sign in — the built-in password login plus external identity providers via OIDC, SAML, LDAP or Kerberos.')"
      :cta-label="t('admin.loginProviders.add', {}, 'Add Provider')"
      @cta="openAddDialog"
    />

    <!-- Row context menu -->
    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil"
        @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem
        v-if="selectedProvider"
        :label="selectedProvider.Enabled
          ? t('admin.loginProviders.disable', {}, 'Disable')
          : t('admin.loginProviders.enable', {}, 'Enable')"
        :icon="selectedProvider.Enabled ? 'circle-pause' : 'circle-play'"
        :disabled="selectedProvider.IsBuiltIn"
        @clicked="toggleEnabled"
      />
      <CoarMenuDivider />
      <CoarMenuItem
        :label="selectedDeleteStaged
          ? t('admin.realmConfig.undelete', {}, 'Undo delete')
          : t('common.delete', {}, 'Delete')"
        :icon="selectedDeleteStaged ? 'undo-2' : 'trash-2'"
        :disabled="!selectedProvider || selectedProvider.IsBuiltIn"
        @clicked="deleteSelected" />
      <CoarMenuDivider v-if="exportMenuVisible" />
      <CoarMenuItem v-if="exportMenuVisible" :label="exportMenuLabel" icon="list-checks"
        @clicked="exportMenuToggle" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('admin.loginProviders.add', {}, 'Add Provider')" icon="plus"
        @clicked="openAddDialog" />
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
