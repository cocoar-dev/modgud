<script setup lang="ts">
/**
 * Inline-editable `key → string[]` map editor, built on the shared data-grid
 * (same component the admin lists use, mirroring EditableStringList). Used for
 * SAML AttributeMap (logical claim name → IdP attribute URIs) and AmrMapping
 * (AuthnContextClassRef URI → AMR values) so admins can fine-tune the mapping
 * when an IdP changes a claim URI instead of being stuck with seeded defaults.
 *
 * Each row's value list is edited as a comma-separated string and split on
 * emit. Rows re-initialise from `modelValue` only when `reloadKey` changes
 * (provider/flavor switch) to avoid clobbering an in-progress edit.
 */
import { ref, watch } from 'vue'
import { CoarDataGridPanel, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

const props = withDefaults(defineProps<{
  modelValue: Record<string, string[]> | undefined
  keyLabel: string
  valueLabel: string
  keyPlaceholder?: string
  valuePlaceholder?: string
  addLabel: string
  searchPlaceholder?: string
  /** Re-init rows when this changes (e.g. provider id / flavor key). */
  reloadKey?: string
}>(), { keyPlaceholder: '', valuePlaceholder: '', reloadKey: '' })

const emit = defineEmits<{
  (e: 'update:modelValue', value: Record<string, string[]>): void
}>()

const { t } = useI18n()

interface Row { id: string; key: string; values: string }

let counter = 0
const newId = () => `cm-${Date.now()}-${counter++}`

const rows = ref<Row[]>([])
const search = ref('')

function fromMap(m: Record<string, string[]> | undefined): Row[] {
  return Object.entries(m ?? {}).map(([key, values]) => ({
    id: newId(),
    key,
    values: (Array.isArray(values) ? values : []).join(', '),
  }))
}

function emitChange() {
  const out: Record<string, string[]> = {}
  for (const r of rows.value) {
    const k = r.key.trim()
    if (!k) continue
    out[k] = r.values.split(',').map((s) => s.trim()).filter(Boolean)
  }
  emit('update:modelValue', out)
}

watch(
  () => props.reloadKey,
  () => { rows.value = fromMap(props.modelValue) },
  { immediate: true },
)

function addRow() {
  rows.value = [...rows.value, { id: newId(), key: '', values: '' }]
  // No emit — an empty row doesn't count until the user types + commits.
}

function removeRow(id: string) {
  rows.value = rows.value.filter((r) => r.id !== id)
  emitChange()
}

const builder = CoarGridBuilder.create<Row>()
  .rowDataRef(rows)
  .option('getRowId', (p: any) => p.data.id)
  .stopEditingWhenCellsLoseFocus(true)
  .columns([
    (col) =>
      col
        .text('key', (c) => c.placeholder(props.keyPlaceholder))
        .editable(true)
        .header(props.keyLabel)
        .flex(1),
    (col) =>
      col
        .wrap(
          col
            .text('values', (c) => c.placeholder(props.valuePlaceholder))
            .editable(true)
            .header(props.valueLabel)
            .flex(2),
        )
        .right({
          icon: 'trash-2',
          size: 's',
          color: 'var(--coar-text-neutral-secondary, #9ca3af)',
          tooltip: t('common.delete', {}, 'Delete'),
          onClick: (row) => removeRow(row.id),
        }),
  ])
  .onCellValueChanged(() => emitChange())
</script>

<template>
  <div class="claim-map">
    <CoarDataGridPanel
      v-model:search="search"
      :builder="builder"
      :search-placeholder="searchPlaceholder ?? t('common.search', {}, 'Suchen…')"
      bordered>
      <template #actions>
        <CoarButton size="s" icon-start="plus" variant="ghost" @click="addRow">
          {{ addLabel }}
        </CoarButton>
      </template>
    </CoarDataGridPanel>
  </div>
</template>

<style scoped>
.claim-map {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
}
</style>
