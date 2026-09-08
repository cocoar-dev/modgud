<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton, CoarContextMenu, CoarMenuItem, CoarMenuDivider, CoarNotice,
  useContextMenu, useDialog,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { useRealmPagesApi, type RealmSlotDto } from '@/composables/usePagesApi'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
const ui = useUI()
const router = useRouter()
const dialog = useDialog()
const api = useRealmPagesApi()

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  ctx.header.subTitle = t('admin.customization.pages.title', {}, 'Pages')
  ctx.header.icon = 'layout-template'
  ctx.content.container = false
}), { immediate: true })

const SLOT_LABELS: Record<string, string> = {
  login: t('admin.customization.pages.login.title', {}, 'Login'),
  logout: t('admin.customization.pages.logout.title', {}, 'Logout'),
  'password-forgot': t('admin.customization.pages.passwordForgot.title', {}, 'Forgot password'),
  consent: t('admin.customization.pages.consent.title', {}, 'Consent'),
}
const CREATABLE_SLOTS = ['login', 'logout', 'password-forgot', 'consent']

interface VariantRow {
  Id: string
  Name: string
  Slug: string
  SlugLabel: string
  RealmActive: boolean
  UsedByApps: string[]
  UsedByCount: number
  UpdatedAt: string | null
  PublishStatus: string
  PublishedRevision: number
}

const rows = ref<VariantRow[]>([])
const error = ref<string | null>(null)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selected = ref<VariantRow | null>(null)

async function reload() {
  try {
    const { Slots } = await api.listSlots()
    rows.value = (Slots as RealmSlotDto[]).flatMap((s) =>
      s.Variants.map((v) => ({
        Id: v.Id,
        Name: v.Name,
        Slug: s.Slug,
        SlugLabel: SLOT_LABELS[s.Slug] ?? s.Slug,
        RealmActive: v.RealmActive,
        UsedByApps: v.UsedByApps,
        UsedByCount: v.UsedByApps.length + (v.RealmActive ? 1 : 0),
        // UpdatedAt only records draft edits, so a variant that was created
        // and published without ever being re-saved has none. Fall back so the
        // column shows when the variant last changed rather than an empty dash.
        UpdatedAt: v.UpdatedAt ?? v.PublishedAt ?? v.CreatedAt,
        PublishStatus: v.IsPublished
          ? v.HasUnpublishedChanges
            ? t('admin.customization.pages.statusDraftChanged', {}, 'Draft changed')
            : t(
                'admin.customization.pages.statusPublished',
                { revision: v.PublishedRevision },
                `Published r${v.PublishedRevision}`,
              )
          : t('admin.customization.pages.statusNotPublished', {}, 'Not published'),
        PublishedRevision: v.PublishedRevision,
      })))
  } catch (e: any) { error.value = e?.message ?? String(e) }
}

onMounted(reload)

function usedByTooltip(r: VariantRow): string {
  const parts: string[] = []
  if (r.RealmActive) parts.push(t('admin.customization.pages.usedRealm', {}, 'Realm (active)'))
  parts.push(...r.UsedByApps)
  return parts.length
    ? parts.join(', ')
    : t('admin.customization.pages.usedNone', {}, 'Not used anywhere')
}

// Formatted in the UI language, not the browser's: the app has its own
// language switch, so a German page showing 9/6/2026, 6:13:32 PM is wrong.
function fmtDate(v: string | null): string {
  if (!v) return '—'
  try { return new Date(v).toLocaleString(language.value) } catch { return v }
}

function newVariant(slug: string) {
  router.push(`/platform/customization/pages/${slug}/new`)
}

function editVariant(row: VariantRow | null) {
  if (row) router.push(`/platform/customization/pages/${row.Slug}/${row.Id}`)
}

// Activation lives here, next to the variants, because it is the same decision
// as authoring one: Realm settings keeps the slot-first view for people who
// arrive from the other direction. Both write the same `{slug}/active` pointer.
async function activateForRealm(row: VariantRow | null) {
  if (!row || row.RealmActive) return
  const confirmed = await dialog.confirm({
    title: t('admin.customization.pages.activateTitle', {}, 'Activate for realm'),
    message: t('admin.customization.pages.activateMessage', { name: row.Name, type: row.SlugLabel },
      `"${row.Name}" goes live for every application that inherits its ${row.SlugLabel} page from the realm. Unpublished changes are published with it.`),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmText: t('admin.customization.pages.activate', {}, 'Activate'),
  }).result
  if (!confirmed) return
  try {
    await api.setActive(row.Slug, row.Id)
    error.value = null
    await reload()
  } catch (e: any) { error.value = e?.message ?? String(e) }
}

async function resetToBuiltIn(row: VariantRow | null) {
  if (!row || !row.RealmActive) return
  const confirmed = await dialog.confirm({
    title: t('admin.customization.pages.deactivateTitle', {}, 'Reset to built-in'),
    message: t('admin.customization.pages.deactivateMessage', { name: row.Name, type: row.SlugLabel },
      `The ${row.SlugLabel} slot falls back to the built-in view. "${row.Name}" is kept and can be activated again.`),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmText: t('admin.customization.pages.deactivate', {}, 'Reset'),
  }).result
  if (!confirmed) return
  try {
    await api.setActive(row.Slug, null)
    error.value = null
    await reload()
  } catch (e: any) { error.value = e?.message ?? String(e) }
}

async function deleteVariant(row: VariantRow | null) {
  if (!row) return
  const confirmed = await dialog.confirm({
    title: t('admin.customization.pages.deleteTitle', {}, 'Delete page'),
    message: t('admin.customization.pages.deleteMessageV2', { name: row.Name, count: String(row.UsedByCount) },
      `Delete "${row.Name}"? It is currently used in ${row.UsedByCount} place(s), which will revert to the built-in view.`),
    cancelText: t('common.cancel', {}, 'Cancel'),
    confirmText: t('common.delete', {}, 'Delete'),
    confirmVariant: 'danger',
  }).result
  if (!confirmed) return
  try {
    await api.deleteVariant(row.Slug, row.Id)
    await reload()
  } catch (e: any) { error.value = e?.message ?? String(e) }
}

// The toolbar button opens the same create menu as the right-click. It has to
// wait for the current click to finish propagating: the menu closes on any
// document click, so opening it synchronously would let this very click shut
// it again. Stopping propagation on the component's `click` would not help —
// that modifier governs the emitted Vue event, not the native DOM one.
//
// Deferred with a timer rather than requestAnimationFrame on purpose: rAF does
// not fire while the page is not being painted (hidden tab, occluded window),
// so the menu would silently never open there.
function openCreateMenu(event: MouseEvent) {
  const { clientX, clientY } = event
  setTimeout(() => viewportMenu.open({ clientX, clientY }), 0)
}

const builder = applyListGridDefaults(CoarGridBuilder.create<VariantRow>(), { openable: true })
  .persistColumnState('platform-pages')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => { if (event.data) editVariant(event.data) })
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) { event.api.deselectAll(); event.node.setSelected(true) }
    selected.value = (event.api.getSelectedRows() as VariantRow[])[0] ?? null
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event: MouseEvent) => viewportMenu.open($event))
  .columns([
    (col: any) => col.field('Name').header('Name', 'common.name').flex(1).minWidth(180),
    (col: any) => col.field('SlugLabel').header('Type', 'admin.customization.pages.type').width(180),
    (col: any) => col.field('RealmActive').header('Live', 'admin.customization.pages.live').width(150)
      .option('valueGetter', (p: any) => p.data?.RealmActive
        ? t('admin.customization.pages.liveRealm', {}, 'Realm')
        : t('admin.customization.pages.liveNo', {}, '—')),
    (col: any) => col.field('UsedByCount').header('Used By', 'admin.customization.pages.usedBy').width(140)
      .option('tooltipValueGetter', (p: any) => p.data ? usedByTooltip(p.data) : ''),
    (col: any) => col.field('PublishStatus').header('Status', 'common.status').width(160),
    (col: any) => col.field('UpdatedAt').header('Updated', 'common.updated').width(200)
      .option('valueGetter', (p: any) => fmtDate(p.data?.UpdatedAt)),
  ])
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4 gap-2">
    <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

    <p class="hint">
      {{ t('admin.customization.pages.hintV4', {}, 'Author page variants here and activate one per type. "Live" shows what the realm currently serves; a slot without a live variant uses the built-in view. Individual applications can pick a different variant in their own settings.') }}
    </p>

    <CoarDataGrid :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <!-- Opens the same menu as the right-click, so the button is not a
             second, poorer path that can only create one of the four types. -->
        <CoarButton size="s" icon-start="plus" @click="openCreateMenu">
          {{ t('admin.customization.pages.newPage', {}, 'New page') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.edit', {}, 'Edit')" icon="pencil" @clicked="editVariant(selected)" />
      <CoarMenuDivider />
      <CoarMenuItem
        v-if="!selected?.RealmActive"
        :label="t('admin.customization.pages.activateTitle', {}, 'Activate for realm')"
        icon="circle-check"
        @clicked="activateForRealm(selected)" />
      <CoarMenuItem
        v-else
        :label="t('admin.customization.pages.deactivateTitle', {}, 'Reset to built-in')"
        icon="rotate-ccw"
        @clicked="resetToBuiltIn(selected)" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteVariant(selected)" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem
        v-for="slug in CREATABLE_SLOTS"
        :key="slug"
        :label="t('admin.customization.pages.createNew', { type: SLOT_LABELS[slug] }, `Create new ${SLOT_LABELS[slug]} page`)"
        icon="plus"
        @clicked="newVariant(slug)" />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
.hint {
  margin: 0;
  font-size: 0.85rem;
  color: var(--coar-text-neutral-secondary);
}
</style>
