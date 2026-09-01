<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  CoarCheckbox,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
  CoarMenuDivider,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useOAuthClientStore } from '@/stores/oauthClient.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useServiceAccountStore } from '@/stores/serviceAccount.store'
import { usePositionStore } from '@/stores/position.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { useClone, buildClonePrefill, CLIENT_CLONE } from '@/composables/useClone'
import { useDraftListOverlay, type DraftRow } from '@/composables/useDraftStaging'
import { useRouter } from 'vue-router'
import type { OAuthClientDto } from '@/models/oauth'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const { stage } = useClone()
const store = useOAuthClientStore()
const appCtx = useAppContextStore()
const saStore = useServiceAccountStore()
const positionStore = usePositionStore()
const appConfig = useAppConfigStore()
const router = useRouter()

// Resolve LinkedServiceAccountId → AccountName for the M2M column. Built
// reactively so SignalR-driven SA changes update the column live without
// a manual reload.
const saNameById = computed(() => {
  const map = new Map<string, string>()
  for (const sa of saStore.entities) {
    map.set(sa.Id, sa.AccountName)
  }
  return map
})

function saNameFor(client: OAuthClientDto): string | null {
  if (!client.LinkedServiceAccountId) return null
  return saNameById.value.get(client.LinkedServiceAccountId) ?? client.LinkedServiceAccountId
}

// Resolve LinkedPositionPrincipalId → AccountName for the Terminal column —
// the position counterpart of the M2M column. Falls back to the raw id when
// the position list is not loaded (feature off / missing position:read).
const positionNameById = computed(() => {
  const map = new Map<string, string>()
  for (const p of positionStore.entities) {
    map.set(p.Id, p.AccountName)
  }
  return map
})

function positionNameFor(client: OAuthClientDto): string | null {
  if (!client.LinkedPositionPrincipalId)
    return client.ManagedTerminalEnrollmentId ? `Terminal ${client.ManagedTerminalEnrollmentId}` : null
  return positionNameById.value.get(client.LinkedPositionPrincipalId) ?? client.LinkedPositionPrincipalId
}

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.oauthClients.title', {}, 'OAuth Clients')
  ctx.header.icon = 'app-window'
  ctx.content.container = false
}), { immediate: true })

const showDcrOnly = ref(false)
const liveRows = computed(() =>
  store.clients
    .filter((c) => appCtx.matchesAppIdList(c.AppIds))
    .filter((c) => !showDcrOnly.value || c.IsDynamicallyRegistered))

// ADR-0005: draft-merged roster — staged edits overlay their live rows,
// draft-created clients appear as synthetic rows (natural key = client_id).
const str = (v: unknown) => (typeof v === 'string' ? v : '')
const arr = (v: unknown) => (Array.isArray(v) ? (v as string[]) : [])
const rows = useDraftListOverlay<OAuthClientDto>({
  section: 'clients',
  rows: liveRows,
  matchLive: (row, e) => row.ClientId === str(e.ClientId),
  overlay: (row, e) => ({
    ...row,
    DisplayName: str(e.DisplayName) || row.DisplayName,
    ClientType: str(e.ClientType) || row.ClientType,
    Enabled: e.Enabled !== false,
    RedirectUris: arr(e.RedirectUris),
    AllowedGrantTypes: arr(e.AllowedGrantTypes),
  }),
  synthesize: (key, e) => ({
    Id: `draft__${key}`,
    ClientId: str(e.ClientId) || key,
    DisplayName: str(e.DisplayName) || null,
    ClientType: str(e.ClientType) || 'confidential',
    Enabled: e.Enabled !== false,
    RedirectUris: arr(e.RedirectUris),
    AllowedGrantTypes: arr(e.AllowedGrantTypes),
    AppIds: [],
    LinkedServiceAccountId: null,
    LinkedPositionPrincipalId: null,
    ManagedTerminalEnrollmentId: null,
    IsDynamicallyRegistered: false,
  } as unknown as OAuthClientDto),
})
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => store.loaded && rows.value.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<DraftRow<OAuthClientDto>>(), { openable: true })
  .persistColumnState('admin-oauth-clients')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event) => {
    if (event.data) openClient(event.data)
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
    // M2M column — surfaces the linked Service Account's AccountName when
    // present, blank otherwise. Lets the admin spot at a glance which
    // clients are owned by a SA vs user-flow clients without opening each.
    (col) => col.field('LinkedServiceAccountId').header('M2M', 'admin.oauthClients.m2m').width(180)
      .option('valueGetter', (p: any) => p.data ? (saNameFor(p.data as OAuthClientDto) ?? '') : ''),
    // Terminal column — the position counterpart of M2M: surfaces the owning
    // position's AccountName for terminal-managed clients. The device fleet
    // stays countable at a glance without opening each position.
    (col) => col.field('LinkedPositionPrincipalId').header('Terminal', 'admin.oauthClients.terminal').width(180)
      .option('valueGetter', (p: any) => p.data ? (positionNameFor(p.data as OAuthClientDto) ?? '') : ''),
    (col) => col.field('DraftStaged').header('Draft', 'admin.realmConfig.gridCol')
      .valueGetter((p: any) => p.data?.DraftStaged === 'create'
        ? t('admin.realmConfig.gridTag.create', {}, 'Staged (new)')
        : p.data?.DraftStaged === 'update'
          ? t('admin.realmConfig.gridTag.update', {}, 'Staged')
          : '')
      .width(120)
      .classRule('draft-staged-cell', (p: any) => !!p.data?.DraftStaged),
    (col) => col.field('IsDynamicallyRegistered').header('DCR', 'admin.oauthClients.dcr').width(80)
      .option('valueGetter', (p: any) => p.data?.IsDynamicallyRegistered ? '●' : '')
      .option('cellStyle', { textAlign: 'center', color: 'var(--coar-accent-primary, #6366f1)' }),
    (col) => col.tag('Enabled', {
      variantMap: { active: 'success', inactive: 'neutral' },
      i18nPrefix: 'common.statusTag.',
    })
      .header('Enabled', 'admin.oauthClients.enabled').width(110)
      .option('valueGetter', (p: any) => p.data?.Enabled === false ? 'inactive' : 'active'),
    (col) => col.field('RedirectUris').header('Redirects', 'admin.oauthClients.redirectCount').width(110)
      .option('valueGetter', (p: any) => (p.data?.RedirectUris ?? []).length),
    (col) => col.field('AllowedGrantTypes').header('Grants', 'admin.oauthClients.grantCount').width(110)
      .option('valueGetter', (p: any) => (p.data?.AllowedGrantTypes ?? []).length),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (!confirm(t('common.confirmDelete', {}, 'Really delete?'))) return
  try {
    await store.remove(id)
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
}

// Clone: load the full client, build a prefill with a blank client_id and the
// secret dropped (create mints a fresh one), then open the Create modal.
async function cloneSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  try {
    const source = await store.loadOne(id)
    if (!source) return
    stage(CLIENT_CLONE.entity, buildClonePrefill(source, CLIENT_CLONE.descriptor))
    navigateToModal('create')
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
}

// Pre-load the SA list once so the M2M column can resolve names without
// per-row HTTP fetches. SignalR keeps the store fresh from there.
onMounted(async () => {
  await Promise.all([
    store.initialize(),
    saStore.entities.length === 0 ? saStore.loadAll() : Promise.resolve(),
    // Position names for the Terminal column — only while the feature is on
    // (the endpoints 404 otherwise); a missing position:read just leaves the
    // column showing raw ids.
    appConfig.config.Features.PositionTerminals && positionStore.entities.length === 0
      ? positionStore.loadAll().catch(() => {})
      : Promise.resolve(),
  ])
})

// SA-managed clients are read-only from this grid — their authoritative
// editor lives in the Service-Account modal. Deep-link there on
// double-click instead of opening ClientDetails. Terminal-managed clients
// follow the same rule: their editor is the position modal.
function openClient(client: OAuthClientDto) {
  if (client.LinkedServiceAccountId) {
    router.push(`/admin/service-accounts#${client.LinkedServiceAccountId}`)
    return
  }
  if (client.LinkedPositionPrincipalId) {
    router.push(`/admin/positions#${client.LinkedPositionPrincipalId}`)
    return
  }
  if (client.ManagedTerminalEnrollmentId) {
    navigateToModal(client.Id)
    return
  }
  navigateToModal(client.Id)
}
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarCheckbox v-model="showDcrOnly"
          :label="t('admin.oauthClients.dcrOnly', {}, 'DCR only')"
          :title="t('admin.oauthClients.dcrOnly.help', {}, 'Show only clients minted via /connect/register (RFC 7591). Useful for spotting agent-registered clients separate from admin-created ones.')" />
        <CoarButton size="s" variant="ghost" icon-start="rotate-ccw" @click="store.loadAll()"
          :title="t('admin.oauthClients.refresh.help', {}, 'Reload the list — picks up clients registered out-of-band (DCR via /connect/register, another admin, another tab).')">
          {{ t('common.refresh', {}, 'Refresh') }}
        </CoarButton>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">
          {{ t('common.create', {}, 'Create') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="app-window"
      :title="t('admin.oauthClients.title', {}, 'OAuth Clients')"
      :description="t('admin.oauthClients.emptyHint', {}, 'An OAuth client is an application that signs users in through this IdP or calls its APIs. Register your first app to obtain a client ID and secret.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil"
        @clicked="(() => {
          const id = selectedIds[0]
          if (!id) return
          const client = store.clients.find((c) => c.Id === id)
          if (client) openClient(client)
        })()" />
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
