<script setup lang="ts">
import { ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

/**
 * Inline-editable list of plain strings — one per row.
 *
 * <para>Drop-in replacement for the textarea-newline-list idiom used
 * across the admin views (redirect URIs, allowed CORS origins, scopes,
 * user claims, …). The form's existing validation path stays on the
 * receiving side (Save → service → ErrorOr); this component just owns
 * the entry UX.</para>
 *
 * <para>Empty rows are filtered out of the emitted <c>modelValue</c> so
 * a freshly-added-not-yet-typed row doesn't reach the backend. The
 * empty row stays in the internal grid state until the user fills it
 * or removes it — the diff-check on external updates keeps the watch
 * loop from clobbering the cell mid-edit.</para>
 */
interface Row {
  id: string
  value: string
}

const props = withDefaults(defineProps<{
  modelValue: string[]
  placeholder?: string
  addLabel?: string
  /** Disable the whole control (no edit, no add, no remove). */
  disabled?: boolean
  /** Read-only: show data, hide affordances. Equivalent to disabled for now. */
  readonly?: boolean
  minHeight?: string
}>(), {
  modelValue: () => [],
  minHeight: '12rem',
})

const emit = defineEmits<{
  (e: 'update:modelValue', value: string[]): void
}>()

const { t } = useI18n()

let counter = 0
const newId = () => `row-${Date.now()}-${counter++}`

const rows = ref<Row[]>(props.modelValue.map((v) => ({ id: newId(), value: v })))

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
    rows.value = next.map((v) => ({ id: newId(), value: v }))
  },
  { deep: true },
)

function addRow() {
  rows.value = [...rows.value, { id: newId(), value: '' }]
  // No emit — empty row doesn't count yet. Once user types and commits,
  // onCellValueChanged → emitChange picks it up.
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
  .option('domLayout', 'autoHeight')
  .option('getRowId', (p: any) => p.data.id)
  .stopEditingWhenCellsLoseFocus(true)
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
</script>

<template>
  <div class="editable-string-list" :style="{ minHeight }">
    <CoarDataGrid :builder="builder" bordered>
      <template #toolbar-left>
        <CoarButton
          v-if="!(disabled || readonly)"
          size="s"
          icon-start="plus"
          variant="ghost"
          @click="addRow"
        >
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
</style>
