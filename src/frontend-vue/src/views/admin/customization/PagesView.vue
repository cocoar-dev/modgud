<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton, CoarContextMenu, CoarMenuItem, CoarMenuDivider, useContextMenu, useDialog,
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
}
const CREATABLE_SLOTS = ['login', 'logout', 'password-forgot']

interface VariantRow {
  Id: string
  Name: string
  Slug: string
  SlugLabel: string
  RealmActive: boolean
  UsedByApps: string[]
  UsedByCount: number
  UpdatedAt: string | null
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
        UpdatedAt: v.UpdatedAt,
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

function fmtDate(v: string | null): string {
  if (!v) return '—'
  try { return new Date(v).toLocaleString() } catch { return v }
}

function newVariant(slug: string) {
  router.push(`/platform/customization/pages/${slug}/new`)
}

function editVariant(row: VariantRow | null) {
  if (row) router.push(`/platform/customization/pages/${row.Slug}/${row.Id}`)
}

async function deleteVariant(row: VariantRow | null) {
  if (!row) return
  const confirmed = await dialog.confirm({
    title: t('admin.customization.pages.deleteTitle', {}, 'Delete page'),
    message: t('admin.customization.pages.deleteMessageV2', { name: row.Name, count: String(row.UsedByCount) },
      `Delete "${row.Name}"? It is currently used in ${row.UsedByCount} place(s), which will revert to the built-in view.`),
    confirmText: t('common.delete', {}, 'Delete'),
    confirmVariant: 'danger',
  }).result
  if (!confirmed) return
  try {
    await api.deleteVariant(row.Slug, row.Id)
    await reload()
  } catch (e: any) { error.value = e?.message ?? String(e) }
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
    (col: any) => col.field('UsedByCount').header('Used By', 'admin.customization.pages.usedBy').width(140)
      .option('tooltipValueGetter', (p: any) => p.data ? usedByTooltip(p.data) : ''),
    (col: any) => col.field('UpdatedAt').header('Updated', 'common.updated').width(200)
      .option('valueGetter', (p: any) => fmtDate(p.data?.UpdatedAt)),
  ])
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4 gap-2">
    <p class="hint">
      {{ t('admin.customization.pages.hintV3', {}, 'Author page variants here, then choose which is live in Realm settings (and per Application). Right-click to create a new page.') }}
    </p>

    <CoarDataGrid :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="newVariant('login')">
          {{ t('admin.customization.pages.newLogin', {}, 'New login page') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.edit', {}, 'Edit')" icon="pencil" @clicked="editVariant(selected)" />
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
