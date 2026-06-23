<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useInviteCodeStore } from '@/stores/inviteCode.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useApplicationsStore } from '@/stores/applications.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import type { InviteCodeDto } from '@/models/inviteCode'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useInviteCodeStore()
// Same shared header App selector the Clients / Scopes / APIs grids use: the
// grid loads ALL codes once and filters client-side by the selection.
const appCtx = useAppContextStore()
const applicationsStore = useApplicationsStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.inviteCodes.title', {}, 'Invite Codes')
  ctx.header.icon = 'ticket'
  ctx.content.container = false
}), { immediate: true })

// Resolve AppId → DisplayName for the App column, reactively (so a SignalR-driven
// app change updates the column without a manual reload).
const appNameById = computed(() => {
  const map = new Map<string, string>()
  for (const a of applicationsStore.apps) map.set(a.Id, a.DisplayName)
  return map
})

// Filter by the header App selection (all / global / a specific app), exactly
// like the other grids. Invite codes are always app-bound, so 'global' (no AppId)
// matches nothing — that's expected.
const rows = computed(() => store.codes.filter((c) => appCtx.matchesSingleAppId(c.AppId)))
const cellMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const showEmpty = computed(() => store.loaded && store.codes.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<InviteCodeDto>())
  .persistColumnState('admin-invite-codes')
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
    selectedIds.value = event.api.getSelectedRows().map((r: InviteCodeDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    (col) => col.field('AppId').header('App', 'admin.inviteCodes.app').flex(1).minWidth(140)
      .option('valueGetter', (p: any) => p.data ? (appNameById.value.get(p.data.AppId) ?? p.data.AppId) : ''),
    (col) => col.field('BoundEmail').header('Bound to', 'admin.inviteCodes.boundEmail').flex(1).minWidth(180)
      .option('valueGetter', (p: any) => p.data?.BoundEmail ?? t('admin.inviteCodes.bearer', {}, 'Bearer (anyone)')),
    (col) => col.field('Status').header('Status', 'admin.inviteCodes.status').width(110),
    (col) => col.field('CreatedAt').header('Created', 'admin.inviteCodes.createdAt').width(180)
      .option('valueGetter', (p: any) => p.data ? new Date(p.data.CreatedAt).toLocaleString() : ''),
    (col) => col.field('ExpiresAt').header('Expires', 'admin.inviteCodes.expiresAt').width(180)
      .option('valueGetter', (p: any) => p.data ? new Date(p.data.ExpiresAt).toLocaleString() : ''),
    (col) => col.field('CreatedBySubject').header('Created by', 'admin.inviteCodes.createdBy').flex(1).minWidth(160),
  ])

async function revokeSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const row = store.codes.find((c) => c.Id === id)
  if (!row) return
  if (row.Status !== 'Open') {
    alert(t('admin.inviteCodes.revoke.onlyOpen', {}, 'Only unused (Open) codes can be revoked.'))
    return
  }
  if (!confirm(t('admin.inviteCodes.revoke.confirm', {}, 'Revoke this invite code?'))) return
  try {
    // Revoke against the code's OWN app — works regardless of the header filter.
    await store.revoke(row.AppId, id)
  } catch (e: any) {
    alert(e?.body?.Message ?? e?.message ?? String(e))
  }
}

onMounted(() => {
  store.initialize() // load all + subscribe to the live change stream
  applicationsStore.initialize() // for the App-name column
})
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder"
      show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" variant="ghost" icon-start="rotate-ccw" @click="store.refresh()">
          {{ t('common.refresh', {}, 'Refresh') }}
        </CoarButton>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('mint')">
          {{ t('admin.inviteCodes.mint', {}, 'Mint codes') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="ticket"
      :title="t('admin.inviteCodes.title', {}, 'Invite Codes')"
      :description="t('admin.inviteCodes.emptyHint', {}, 'No invite codes yet. Under the InviteCode self-registration posture, an unknown email can only sign up by presenting a valid code. Mint a batch to hand out.')"
      :cta-label="t('admin.inviteCodes.mint', {}, 'Mint codes')"
      @cta="navigateToModal('mint')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('admin.inviteCodes.revoke', {}, 'Revoke')" icon="trash-2" @clicked="revokeSelected" />
    </CoarContextMenu>
  </div>
</template>
