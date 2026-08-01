<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { CoarDataGrid, CoarDataGridPanel, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton, CoarIcon, CoarPopover } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

/**
 * Editable list of plain strings — one per row. Supports the standard data
 * grid and a compact grid whose toolbar acts as the list header.
 *
 * <para>Drop-in replacement for the textarea-newline-list idiom used
 * across the admin views (redirect URIs, allowed CORS origins, scopes,
 * user claims, …). The form's existing validation path stays on the
 * receiving side (Save → service → ErrorOr); this component just owns
 * the entry UX.</para>
 *
 * <para>Empty rows are filtered out of the emitted <c>modelValue</c> so
 * a freshly-added-not-yet-typed row doesn't reach the backend. The
 * empty row stays in internal state until the user fills it or removes it —
 * the diff-check on external updates keeps the watch loop from clobbering an
 * in-progress edit.</para>
 */
interface Row {
  id: string
  value: string
}

const props = withDefaults(defineProps<{
  modelValue: string[]
  placeholder?: string
  searchPlaceholder?: string
  /** Label rendered on the left of the compact-grid toolbar. */
  headerLabel?: string
  /** Optional help text rendered from an info icon beside the toolbar label. */
  headerHint?: string
  addLabel?: string
  /** Disable the whole control (no edit, no add, no remove). */
  disabled?: boolean
  /** Read-only: show data, hide affordances. Equivalent to disabled for now. */
  readonly?: boolean
  minHeight?: string
  /** Grow to the height supplied by the parent instead of using a fixed viewport. */
  fillAvailable?: boolean
  /** Compact grid hides the technical column header and empty-row overlay. */
  appearance?: 'grid' | 'compact-grid' | 'panel-grid'
}>(), {
  modelValue: () => [],
  minHeight: '12rem',
  appearance: 'grid',
})

const emit = defineEmits<{
  (e: 'update:modelValue', value: string[]): void
}>()

const { t } = useI18n()

let counter = 0
const newId = () => `row-${Date.now()}-${counter++}`

function rowsFrom(values: string[]): Row[] {
  return values.map((value) => ({ id: newId(), value }))
}

const rows = ref<Row[]>(rowsFrom(props.modelValue))

function filteredEmit(): string[] {
  return rows.value.map((r) => r.value.trim()).filter((v) => v.length > 0)
}

function emitChange() {
  emit('update:modelValue', filteredEmit())
}

// External modelValue change → re-sync internal rows. Only fires when
// the incoming array differs from what we'd emit, so the parent's own
// reactive roundtrip doesn't blow away an in-progress edit (which has
// an empty Row.value the parent never saw).
watch(
  () => props.modelValue,
  (next) => {
    const emitted = filteredEmit()
    if (JSON.stringify(next) === JSON.stringify(emitted)) return
    rows.value = rowsFrom(next)
  },
  { deep: true },
)

function addRow() {
  const row = { id: newId(), value: '' }
  rows.value = [...rows.value, row]
  // No emit — an empty row does not count yet. Start editing immediately in
  // the compact form-grid so Add behaves like inserting a new input row.
  if (props.appearance !== 'grid') {
    nextTick(() => {
      const rowIndex = rows.value.findIndex((candidate) => candidate.id === row.id)
      builder.api?.ensureIndexVisible(rowIndex)
      builder.api?.startEditingCell({ rowIndex, colKey: 'value' })
    })
  }
}

function removeRow(id: string) {
  rows.value = rows.value.filter((r) => r.id !== id)
  emitChange()
}

const builder = CoarGridBuilder.create<Row>()
  .rowDataRef(rows)
  // Size the grid to its rows. Without this the grid sits in a flex/min-height
  // wrapper that it does not fill, so AG Grid's root+viewport collapse to 0px
  // height and pre-loaded rows render into the DOM but are clipped to nothing
  // (e.g. a realm's existing domains showed in the list grid but not in the
  // edit modal). autoHeight makes the grid grow with its content instead.
  // The compact form variant keeps a stable viewport and scrolls its rows.
  // Other usages retain the existing grow-to-content behaviour.
  .option('domLayout', props.appearance === 'grid' ? 'autoHeight' : 'normal')
  .option('getRowId', (p: any) => p.data.id)
  .stopEditingWhenCellsLoseFocus(true)

if (props.appearance !== 'grid') {
  builder
    .option('headerHeight', 0)
    .option('suppressNoRowsOverlay', true)
    .option('singleClickEdit', true)
}

builder
  .columns([
    (col) =>
      col
        .wrap(
          col
            .text('value', (t) => t.placeholder(props.placeholder ?? ''))
            .editable(!(props.disabled || props.readonly))
            .flex(1),
        )
        .right({
          icon: 'trash-2',
          size: 's',
          color: 'var(--coar-text-neutral-secondary, #9ca3af)',
          tooltip: t('common.delete', {}, 'Delete'),
          show: () => !(props.disabled || props.readonly),
          onClick: (row) => removeRow(row.id),
        }),
  ])
  .onCellValueChanged(() => emitChange())

const search = ref('')
</script>

<template>
  <div
    class="editable-string-list"
    :class="{
      'editable-string-list--compact-grid': appearance !== 'grid',
      'editable-string-list--fill': appearance !== 'grid' && fillAvailable,
    }"
    :style="appearance !== 'grid'
      ? fillAvailable ? undefined : { height: minHeight, maxHeight: minHeight }
      : { minHeight }">
    <CoarDataGridPanel
      v-if="appearance === 'panel-grid'"
      v-model:search="search"
      :builder="builder"
      :search-placeholder="searchPlaceholder ?? t('common.search', {}, 'Search…')"
      bordered>
      <template #actions>
        <CoarButton
          v-if="!(disabled || readonly)"
          size="s"
          icon-start="plus"
          variant="ghost"
          @click="addRow">
          {{ addLabel ?? t('common.add', {}, 'Add') }}
        </CoarButton>
      </template>
    </CoarDataGridPanel>

    <CoarDataGrid v-else :builder="builder" bordered>
      <template #toolbar-left>
        <div v-if="appearance === 'compact-grid'" class="compact-grid-heading">
          <span class="compact-grid-title">{{ headerLabel }}</span>
          <CoarPopover v-if="headerHint" mode="both" :offset="6">
            <button
              type="button"
              class="compact-grid-help"
              :aria-label="`${t('common.info', {}, 'Info')}: ${headerLabel}`">
              <CoarIcon name="info" size="s" aria-hidden="true" />
            </button>
            <template #content>
              <p class="compact-grid-help-text">{{ headerHint }}</p>
            </template>
          </CoarPopover>
        </div>
        <CoarButton
          v-else-if="!(disabled || readonly)"
          size="s"
          icon-start="plus"
          variant="ghost"
          @click="addRow"
        >
          {{ addLabel ?? t('common.add', {}, 'Add') }}
        </CoarButton>
      </template>

      <template v-if="appearance === 'compact-grid'" #toolbar-right>
        <CoarButton
          v-if="!(disabled || readonly)"
          size="s"
          icon-start="plus"
          variant="ghost"
          @click="addRow">
          {{ addLabel ?? t('common.add', {}, 'Add') }}
        </CoarButton>
      </template>
    </CoarDataGrid>
  </div>
</template>

<style scoped>
.editable-string-list {
  display: flex;
  flex-direction: column;
  min-height: 12rem;
}

.editable-string-list--compact-grid {
  min-height: 0;
}

.editable-string-list--fill {
  height: 100%;
  max-height: none;
}

.editable-string-list--compact-grid :deep(.ag-theme-cocoar--bordered > .coar-grid-toolbar) {
  min-height: 2.75rem;
  margin: 0;
  padding: 0.35rem 0.6rem;
}

.compact-grid-heading {
  display: inline-flex;
  align-items: center;
  min-width: 0;
  gap: 0.35rem;
}

.compact-grid-title {
  overflow: hidden;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.compact-grid-heading :deep(.coar-popover),
.compact-grid-heading :deep(.coar-popover-trigger) {
  display: inline-flex;
  align-items: center;
  height: 1.5rem;
  line-height: 1;
}

.compact-grid-help {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1rem;
  height: 1rem;
  padding: 0;
  border: 0;
  color: var(--coar-text-neutral-tertiary, #6b7280);
  background: transparent;
  cursor: help;
}

.compact-grid-help:focus-visible {
  border-radius: 50%;
  outline: 2px solid var(--coar-border-brand-primary, #009fe3);
  outline-offset: 2px;
}

.compact-grid-help-text {
  width: min(22rem, calc(100vw - 2rem));
  margin: 0;
  padding: 0.75rem;
  color: var(--coar-text-neutral-secondary, #525e76);
  font-size: 0.8rem;
  line-height: 1.45;
  white-space: pre-line;
}
</style>
