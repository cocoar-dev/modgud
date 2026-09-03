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
import { useUserStore } from '@/stores/user.store'
import { useRealmDraftStore, type ManifestEntity } from '@/stores/realmDraft.store'
import { useDraftStaging } from '@/composables/useDraftStaging'
import { useExportSelectionMenu } from '@/composables/useExportSelectionMenu'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import type { UserDto } from '@/models/user'
import SetPasswordModal from './SetPasswordModal.vue'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const userStore = useUserStore()
const adminHttp = useHttpClient('/api/admin/users')

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.users.title', {}, 'Users')
  ctx.header.icon = 'users'
  ctx.content.container = false
}), { immediate: true })

// ── ADR-0005 draft overlay ────────────────────────────────────────────────────
// While a draft is checked out, the roster shows the STAGED state: edited rows
// carry the staged profile values, users created in the draft appear as
// synthetic rows (no live id yet). The plan (authoritative diff) drives which
// rows are marked; live-only concerns (recycle bin, sessions) stay untouched.
const draftStore = useRealmDraftStore()
const staging = useDraftStaging('users')

type UserRow = UserDto & { DraftStaged?: 'create' | 'update' | 'delete' }

const users = computed(() => userStore.entities)

// Recycle-bin reveal: pending-deletion users (self-service grace OR admin
// bin) are hidden by default to keep the active roster clean; the toolbar
// toggle reveals them inline with a lifecycle badge.
const showRecycleBin = ref(false)
const filteredUsers = computed(() =>
  showRecycleBin.value ? users.value : users.value.filter(u => !u.IsDeletionPending))
const pendingCount = computed(() => users.value.filter(u => u.IsDeletionPending).length)

const displayUsers = computed<UserRow[]>(() => {
  const base = filteredUsers.value as UserRow[]
  const draft = draftStore.current
  const plan = draftStore.plan
  if (!draft || !plan) return base

  const manifestUsers = (draft.Manifest.Users ?? []) as ManifestEntity[]
  const entityByKey = new Map(manifestUsers.map((u) =>
    [String(u.Key ?? u.UserName ?? u.Email ?? ''), u]))
  const entries = plan.Sections.find((sec) => sec.Name === 'users')?.Entries ?? []

  const str = (v: unknown) => (typeof v === 'string' ? v : '')
  const overlays = new Map<string, { entity: ManifestEntity }>()
  const created: UserRow[] = []

  for (const entry of entries) {
    if (entry.Action !== 'create' && entry.Action !== 'update') continue
    const entity = entityByKey.get(entry.Key)
    if (!entity) continue
    if (entry.Action === 'update') {
      // Same matching the applier uses: email first, then account name.
      const email = str(entity.Email).toUpperCase()
      const name = str(entity.UserName).toLowerCase()
      const live = base.find((r) =>
        (email && r.Email?.toUpperCase() === email) || (name && r.UserName === name))
      if (live) overlays.set(live.Id, { entity })
      continue
    }
    created.push({
      Id: `draft__${entry.Key}`,
      Firstname: str(entity.Firstname),
      Lastname: str(entity.Lastname),
      Acronym: str(entity.Acronym) || undefined,
      Email: str(entity.Email) || undefined,
      UserName: str(entity.UserName) || str(entity.Email),
      IsActive: true,
      HasPassword: draft.SecretSlots.some((slot) => slot === `users/${entry.Key}/Password`),
      EmailConfirmed: entity.EmailConfirmed === true,
      ExternalLoginProviderIds: [],
      Status: 'Active',
      DraftStaged: 'create',
    })
  }

  // Staged deletions come from the draft itself (the entity is gone from the
  // manifest); the key is whatever the row showed — username or email.
  const deletions = (draft.Deletions ?? []).filter((d) => d.Section === 'users')
  const deleteStaged = (row: UserRow) => deletions.some((d) =>
    d.Key === row.UserName ||
    (!!row.Email && d.Key.toLowerCase() === row.Email.toLowerCase()))

  const rows = base.map((row) => {
    if (deleteStaged(row)) return { ...row, DraftStaged: 'delete' as const }
    const overlay = overlays.get(row.Id)
    if (!overlay) return row
    const e = overlay.entity
    return {
      ...row,
      Firstname: str(e.Firstname) || row.Firstname,
      Lastname: str(e.Lastname) || row.Lastname,
      Acronym: str(e.Acronym) || row.Acronym,
      Email: str(e.Email) || row.Email,
      UserName: str(e.UserName).toLowerCase() || row.UserName,
      DraftStaged: 'update' as const,
    }
  })
  return [...created, ...rows]
})

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])
const passwordModalUserId = ref<string | null>(null)

const selectedUser = computed(() => {
  const id = selectedIds.value[0]
  return id ? users.value.find(u => u.Id === id) : null
})

const selectedDeleteStaged = computed(() => {
  const id = selectedIds.value[0]
  return !!id && displayUsers.value.find((r) => r.Id === id)?.DraftStaged === 'delete'
})

// selectedUser resolves against the LIVE roster, so draft-only creations
// (not in the export) yield null and hide the menu item automatically.
const { exportMenuVisible, exportMenuLabel, exportMenuToggle } = useExportSelectionMenu('users',
  computed(() => selectedUser.value ? (selectedUser.value.UserName || selectedUser.value.Email || null) : null))

// Onboarding empty-state shows only when the realm genuinely has no users
// (keyed off the raw roster, not the recycle-bin/search-filtered view) so a
// filtered-to-empty grid keeps its toggle + "Keine Einträge" overlay instead.
const showEmpty = computed(() => userStore.allLoaded && users.value.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<UserRow>(), { openable: true })
  .persistColumnState('admin-users')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(displayUsers)
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
    selectedIds.value = event.api.getSelectedRows().map((r: UserDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    // Password-set indicator — pinned first so the admin sees account-setup
    // state at a glance, even with many columns scrolled away.
    (col) => col.icon('HasPassword').header('')
      .valueGetter((p: any) => p.data?.HasPassword ? 'key-round' : '')
      .width(38).resizable(false).pinned('left')
      // Icon cells carry a lucide name as their value — no truncation tooltip.
      .option('tooltipValueGetter', () => null),
    // Identity column — pinned next to the password indicator and
    // emphasized as the row's primary label.
    (col) => col.field('UserName').header('Username', 'admin.users.username')
      .width(150).pinned('left').cellClass('user-name-cell'),
    (col) => col.field('Firstname').header('First Name', 'admin.users.firstname').flex(1),
    (col) => col.field('Lastname').header('Last Name', 'admin.users.lastname').flex(1),
    (col) => col.field('Acronym').header('Acronym', 'admin.users.acronym').width(100),
    (col) => col.tag('IsActive', {
      variantMap: { active: 'success', inactive: 'neutral' },
      i18nPrefix: 'common.statusTag.',
    })
      .header('Active', 'admin.users.active').width(110)
      .option('valueGetter', (p: any) => p.data?.IsActive ? 'active' : 'inactive'),
    // ADR-0005: staged rows (edited or created in the active draft).
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
    // Lifecycle badge — only meaningful for pending-deletion rows (visible
    // when the recycle bin is revealed). Empty for normal active users.
    (col) => col.field('DeletionInitiator').header('Lifecycle', 'admin.users.lifecycle')
      .valueGetter((p: any) => {
        const d = p.data
        if (!d?.IsDeletionPending) return ''
        const who = d.DeletionInitiator === 'Admin'
          ? t('admin.users.binAdmin', {}, 'Recycle bin')
          : t('admin.users.binSelf', {}, 'Self-deletion')
        const when = d.DeletionDeadline ? new Date(d.DeletionDeadline).toLocaleDateString() : ''
        return when ? `${who} · ${when}` : who
      })
      .flex(1)
      .classRule('deletion-pending-cell', (p: any) => !!p.data?.IsDeletionPending),
    // Email — visually de-emphasized when Identity-side EmailConfirmed=false
    // so unverified addresses don't read like authoritative contact info.
    (col) => col.field('Email').header('Email', 'admin.users.email').flex(1)
      .classRule('email-unverified', (p: any) => !!p.data?.Email && !p.data?.EmailConfirmed),
  ])

async function deleteUsers() {
  const first = selectedIds.value[0]
  if (!first) return
  // Draft-created rows exist only in the staged manifest — "delete" = unstage.
  if (first.startsWith('draft__')) {
    await staging.unstage(first.slice('draft__'.length))
    return
  }
  // ADR-0005: ONE rule — deletes are always staged. Apply moves the user into
  // the recycle bin (grace + restore unchanged); the live emergency lever is
  // "deactivate", not delete. A second delete on a staged row undoes it.
  if (staging.stagingActive.value) {
    const row = displayUsers.value.find((r) => r.Id === first)
    const key = row?.UserName || row?.Email
    if (!row || !key) return
    if (row.DraftStaged === 'delete') return staging.unstageDelete(key)
    if (!confirm(t('admin.users.confirmStagedBin', {},
        'Stage the deletion? On apply the user is moved to the recycle bin (deactivated, scheduled for deletion, restorable until erased).'))) return
    return staging.stageDelete(key)
  }
  if (confirm(t('admin.users.confirmBin', {},
      'Move to the recycle bin? The user is deactivated and scheduled for deletion, but can be restored until it is permanently erased.'))) {
    await userStore.binUsers(selectedIds.value)
  }
}

async function restoreSelected() {
  if (selectedIds.value.length > 0 && confirm(t('admin.users.confirmRestore', {},
      'Restore from the recycle bin? The pending deletion is cancelled and the user is reactivated.'))) {
    await userStore.restoreUsers(selectedIds.value)
  }
}

async function forceDeleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  const reason = window.prompt(t('admin.users.forceDeleteReason', {},
    'Reason for permanent deletion (recorded in the audit log):'), '')
  if (reason === null) return
  if (!confirm(t('admin.users.confirmForceDelete', {},
      'Permanently erase this user now? This empties the recycle bin for them and CANNOT be undone.'))) return
  await userStore.forceDelete(id, reason.trim() || 'Admin force-delete from recycle bin')
}

const magicLinkSending = ref(false)
const magicLinkResult = ref<{ ok: boolean; message: string } | null>(null)

async function sendMagicLink() {
  const id = selectedIds.value[0]
  if (!id || magicLinkSending.value) return
  magicLinkSending.value = true
  magicLinkResult.value = null
  try {
    await adminHttp.addPath(id, 'magic-link').post()
    magicLinkResult.value = { ok: true, message: t('admin.users.magicLinkSent', {}, 'Magic link sent.') }
  } catch (e: any) {
    magicLinkResult.value = { ok: false, message: e?.data?.Message || t('admin.users.magicLinkFailed', {}, 'Failed to send magic link.') }
  } finally {
    magicLinkSending.value = false
    setTimeout(() => magicLinkResult.value = null, 5000)
  }
}

onMounted(() => {
  userStore.initialize()
})
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <label class="recycle-bin-toggle" :title="t('admin.users.showRecycleBinHint', {}, 'Reveal users pending deletion')">
          <input type="checkbox" v-model="showRecycleBin" />
          <span>{{ t('admin.users.showRecycleBin', {}, 'Show recycle bin') }}</span>
          <span v-if="pendingCount > 0" class="recycle-bin-count">{{ pendingCount }}</span>
        </label>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">{{ t('common.create', {}, 'Create') }}</CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="users"
      :title="t('admin.users.title', {}, 'Users')"
      :description="t('admin.users.emptyHint', {}, 'Users are the people who can sign in to this realm. Create the first account to get started.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <!-- Magic link result toast -->
    <div v-if="magicLinkResult" class="fixed bottom-4 right-4 z-50 rounded-lg px-4 py-3 text-sm shadow-lg"
      :class="magicLinkResult.ok ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'">
      {{ magicLinkResult.message }}
    </div>

    <!-- Row context menu -->
    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('admin.users.setPassword', {}, 'Set Password')" icon="key" @clicked="passwordModalUserId = selectedIds[0] ?? null" />
      <CoarMenuItem
        :label="t('admin.users.sendMagicLink', {}, 'Send Magic Link')"
        icon="mail"
        :disabled="!selectedUser?.Email"
        @clicked="sendMagicLink"
      />
      <CoarMenuItem
        :label="t('admin.users.showIdpClaims', {}, 'Show IdP Claims')"
        icon="key-round"
        @clicked="selectedIds[0] && navigateToModal(`claims/${selectedIds[0]}`)"
      />
      <CoarMenuDivider />
      <!-- Active users → bin them; pending users → restore or permanently erase. -->
      <CoarMenuItem v-if="!selectedUser?.IsDeletionPending"
        :label="selectedDeleteStaged
          ? t('admin.realmConfig.undelete', {}, 'Undo delete')
          : t('admin.users.bin', {}, 'Delete (recycle bin)')"
        :icon="selectedDeleteStaged ? 'undo-2' : 'trash-2'"
        @clicked="deleteUsers" />
      <template v-else>
        <CoarMenuItem :label="t('admin.users.restore', {}, 'Restore')" icon="rotate-ccw" @clicked="restoreSelected" />
        <CoarMenuItem :label="t('admin.users.forceDelete', {}, 'Delete permanently')" icon="trash-2" @clicked="forceDeleteSelected" />
      </template>
      <CoarMenuDivider v-if="exportMenuVisible" />
      <CoarMenuItem v-if="exportMenuVisible" :label="exportMenuLabel" icon="list-checks"
        @clicked="exportMenuToggle" />
    </CoarContextMenu>

    <!-- Viewport context menu (empty area) -->
    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>

    <!-- Password modal (non-routed): unlike the routed modals it has no overlay
         host to size the panel, so the panel wrapper caps the width here —
         otherwise ModalLayout's width:100%/height:100% container fills the whole
         fixed overlay (i.e. the viewport). -->
    <Teleport to="body">
      <div v-if="passwordModalUserId" class="password-modal-overlay" @click.self="passwordModalUserId = null">
        <div class="password-modal-panel">
          <SetPasswordModal :id="passwordModalUserId" :close="() => passwordModalUserId = null" />
        </div>
      </div>
    </Teleport>
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

.password-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
  background-color: rgba(0, 0, 0, 0.4);
}

/* Cap the modal to a compact form size (mirrors the routed MODAL_MD size).
   The panel bounds the width; overriding ModalLayout's height:100% to auto +
   a viewport cap makes it size to its single input field instead of filling
   the whole overlay. */
.password-modal-panel {
  width: 28rem;
  max-width: calc(100vw - 2rem);
}
.password-modal-panel :deep(.modal-container) {
  height: auto;
  max-height: 85vh;
}

/* AG Grid cells render inside the host component, so style hooks need
   :deep() to reach them from scoped styles. */
:deep(.user-name-cell) {
  font-weight: 600;
}

:deep(.email-unverified) {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-style: italic;
}

/* Lifecycle badge cell — amber, to read as "scheduled for deletion". */
:deep(.deletion-pending-cell) {
  color: #92400e;
  font-weight: 600;
}

.recycle-bin-toggle {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-right: 8px;
  font-size: 0.8rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  cursor: pointer;
  user-select: none;
}
.recycle-bin-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 9px;
  background: #fef3c7;
  color: #92400e;
  font-size: 0.7rem;
  font-weight: 700;
}
</style>
