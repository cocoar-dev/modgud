<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  CoarContextMenu,
  CoarMenuItem,
  CoarNotice,
  useContextMenu,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { usePageCompositionsApi } from '@/composables/usePageCompositionsApi'
import GridEmptyState from '@/components/GridEmptyState.vue'

interface CompositionRow {
  id: string
  name: string
  latestVersion: string
  versionCount: number
}

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
const ui = useUI()
const router = useRouter()
const { repository } = usePageCompositionsApi()

const rows = ref<CompositionRow[]>([])
const loading = ref(true)
const error = ref<string | null>(null)
const selected = ref<CompositionRow | null>(null)
const cellMenu = useContextMenu()

const showEmpty = computed(() => !loading.value && rows.value.length === 0)

watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  ctx.header.subTitle = t('admin.customization.compositions.title', {}, 'Compositions')
  ctx.header.icon = 'copy'
  ctx.content.container = false
}), { immediate: true })

async function load() {
  loading.value = true
  error.value = null
  try {
    rows.value = (await repository.list()).map((item) => ({
      id: item.id,
      name: item.name,
      latestVersion: item.latestVersion,
      versionCount: item.versions?.length ?? 1,
    }))
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    loading.value = false
  }
}

function create() {
  router.push('/platform/customization/compositions/new')
}

function edit(row: CompositionRow | null) {
  if (row) router.push(`/platform/customization/compositions/${encodeURIComponent(row.id)}`)
}

const builder = applyListGridDefaults(CoarGridBuilder.create<CompositionRow>(), { openable: true })
  .persistColumnState('platform-compositions')
  .option('getRowId', (params: any) => params.data.id)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event: any) => edit(event.data ?? null))
  .onCellContextMenu((event: any) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selected.value = (event.api.getSelectedRows() as CompositionRow[])[0] ?? null
    cellMenu.open(event.event as MouseEvent)
  })
  .columns([
    (col: any) => col.field('name')
      .header('Name', 'common.name')
      .flex(2)
      .minWidth(240),
    (col: any) => col.field('latestVersion')
      .header('Latest version', 'admin.customization.compositions.latestVersion')
      .width(160)
      .option('valueGetter', (params: any) => `v${params.data?.latestVersion ?? '—'}`),
    (col: any) => col.field('versionCount')
      .header('Versions', 'admin.customization.compositions.versions')
      .width(140),
  ])

onMounted(load)
</script>

<template>
  <div class="compositions-page">
    <p class="hint">
      {{ t('admin.customization.compositions.hint', {}, 'Create reusable building blocks here. To use one, open a Page, select a container in the Outline and choose Compositions → Insert.') }}
    </p>

    <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

    <CoarDataGrid
      v-show="!showEmpty"
      :builder="builder"
      :search-placeholder="searchPlaceholder"
      show-search
      class="composition-grid"
      bordered
      elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="create">
          {{ t('admin.customization.compositions.new', {}, 'New composition') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="copy"
      :title="t('admin.customization.compositions.title', {}, 'Compositions')"
      :description="t('admin.customization.compositions.empty', {}, 'Create a reusable building block once and place pinned versions of it on multiple pages.')"
      :cta-label="t('admin.customization.compositions.new', {}, 'New composition')"
      @cta="create"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem
        :label="t('common.open', {}, 'Open')"
        icon="pencil"
        @clicked="edit(selected)"
      />
      <CoarMenuItem
        :label="t('admin.customization.compositions.new', {}, 'New composition')"
        icon="plus"
        @clicked="create"
      />
    </CoarContextMenu>
  </div>
</template>

<style scoped>
.compositions-page {
  display: flex;
  flex: 1;
  min-width: 0;
  min-height: 0;
  flex-direction: column;
  gap: 0.5rem;
  padding: 1rem;
}

.hint {
  margin: 0;
  color: var(--coar-text-neutral-secondary);
  font-size: 0.85rem;
}

.composition-grid {
  flex: 1;
  min-height: 0;
}

</style>
