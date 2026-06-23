<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  CoarSelect,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useInviteCodeStore } from '@/stores/inviteCode.store'
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
const applicationsStore = useApplicationsStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.inviteCodes.title', {}, 'Invite Codes')
  ctx.header.icon = 'ticket'
  ctx.content.container = false
}), { immediate: true })

// App picker — invite codes are strictly app-scoped, so the admin first picks
// the application to manage. The choice drives both the list and the mint modal.
const appOptions = computed(() =>
  applicationsStore.apps.map((a) => ({ value: a.Id, label: `${a.DisplayName} (${a.Slug})` })))
const selectedAppId = computed({
  get: () => store.selectedAppId ?? '',
  set: (v: string) => store.setApp(v || null),
})

const rows = computed(() => store.codes)
const cellMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const hasApp = computed(() => !!store.selectedAppId)
const showEmpty = computed(() => hasApp.value && store.loadedAppId === store.selectedAppId && store.codes.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<InviteCodeDto>())
  .persistColumnState('admin-invite-codes')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellContextMenu((event) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selectedIds.value = event.api.getSelectedRows().map((r: InviteCodeDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
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
  if (!id || !store.selectedAppId) return
  const row = store.codes.find((c) => c.Id === id)
  if (row && row.Status !== 'Open') {
    alert(t('admin.inviteCodes.revoke.onlyOpen', {}, 'Only unused (Open) codes can be revoked.'))
    return
  }
  if (!confirm(t('admin.inviteCodes.revoke.confirm', {}, 'Revoke this invite code?'))) return
  try {
    await store.revoke(store.selectedAppId, id)
  } catch (e: any) {
    alert(e?.body?.Message ?? e?.message ?? String(e))
  }
}

// Reload whenever the selected app changes.
watch(() => store.selectedAppId, async (appId) => {
  if (appId) await store.loadForApp(appId)
})

onMounted(async () => {
  store.initialize() // subscribe to the live change stream
  await applicationsStore.initialize()
  // Default to the first app for convenience.
  const firstApp = applicationsStore.apps[0]
  if (!store.selectedAppId && firstApp) {
    store.setApp(firstApp.Id)
  }
})
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="hasApp && !showEmpty" :builder="builder" :search-placeholder="searchPlaceholder"
      show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-left>
        <CoarSelect v-model="selectedAppId" :options="appOptions" style="min-width: 16rem"
          :title="t('admin.inviteCodes.app.help', {}, 'Invite codes belong to one application. Pick which app to manage.')" />
      </template>
      <template #toolbar-right>
        <CoarButton size="s" variant="ghost" icon-start="rotate-ccw" @click="store.refresh()">
          {{ t('common.refresh', {}, 'Refresh') }}
        </CoarButton>
        <CoarButton size="s" icon-start="plus" :disabled="!hasApp" @click="navigateToModal('mint')">
          {{ t('admin.inviteCodes.mint', {}, 'Mint codes') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="hasApp && showEmpty"
      icon="ticket"
      :title="t('admin.inviteCodes.title', {}, 'Invite Codes')"
      :description="t('admin.inviteCodes.emptyHint', {}, 'No invite codes for this app yet. Under the InviteCode self-registration posture, an unknown email can only sign up by presenting a valid code. Mint a batch to hand out.')"
      :cta-label="t('admin.inviteCodes.mint', {}, 'Mint codes')"
      @cta="navigateToModal('mint')"
    />

    <div v-if="!hasApp" class="flex flex-1 items-center justify-center p-8 text-gray-400">
      {{ t('admin.inviteCodes.pickApp', {}, 'Select an application to manage its invite codes.') }}
    </div>

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('admin.inviteCodes.revoke', {}, 'Revoke')" icon="trash-2" @clicked="revokeSelected" />
    </CoarContextMenu>
  </div>
</template>
