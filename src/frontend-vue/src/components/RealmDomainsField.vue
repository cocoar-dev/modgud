<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

/**
 * Realm domains editor with an inline primary-domain picker.
 *
 * <para>A realm routes on any of its <c>domains</c>, but exactly one of them is
 * the <b>primary</b> — its canonical public host. The primary drives every
 * outbound link (invite / magic-link / reset mails) and is the WebAuthn RP ID,
 * so a passkey only works on the primary domain. This control lets the admin
 * edit the domain set and pick which one is primary in one place.</para>
 *
 * <para>The realm-specific primary semantics stay in this component, while
 * its presentation follows the compact editable grids used throughout the
 * admin UI. Primary is tracked by row <i>id</i>, not by value, so renaming the
 * primary domain in place keeps it primary.</para>
 */
interface Row {
  id: string
  value: string
}

const props = withDefaults(
  defineProps<{
    domains: string[]
    primary: string
    disabled?: boolean
    placeholder?: string
  }>(),
  {
    domains: () => [],
    primary: '',
    disabled: false,
    placeholder: '',
  },
)

const emit = defineEmits<{
  (e: 'update:domains', value: string[]): void
  (e: 'update:primary', value: string): void
}>()

const { t } = useI18n()

let counter = 0
const newId = () => `dom-${Date.now()}-${counter++}`

const rows = ref<Row[]>(props.domains.map((v) => ({ id: newId(), value: v })))
// id of the primary row (anchor by id so an in-place rename keeps primary).
const primaryId = ref<string>(resolvePrimaryId(props.primary))

function resolvePrimaryId(primaryValue: string): string {
  const wanted = primaryValue.trim()
  const match = rows.value.find((r) => r.value.trim().length > 0 && r.value.trim() === wanted)
  if (match) return match.id
  const firstFilled = rows.value.find((r) => r.value.trim().length > 0)
  return firstFilled?.id ?? ''
}

function emittedDomains(): string[] {
  return rows.value.map((r) => r.value.trim()).filter((v) => v.length > 0)
}

function emittedPrimary(): string {
  const pr = rows.value.find((r) => r.id === primaryId.value)
  const prVal = pr?.value.trim() ?? ''
  if (prVal.length > 0) return prVal
  // No (or empty) primary row → fall back to the first filled domain.
  return rows.value.find((r) => r.value.trim().length > 0)?.value.trim() ?? ''
}

function emitAll() {
  // Keep primaryId pointing at a filled row so the radio reflects reality.
  const pr = rows.value.find((r) => r.id === primaryId.value)
  if (!pr || pr.value.trim().length === 0) {
    primaryId.value = rows.value.find((r) => r.value.trim().length > 0)?.id ?? ''
  }
  emit('update:domains', emittedDomains())
  emit('update:primary', emittedPrimary())
  nextTick(() => builder.api?.refreshCells({ force: true }))
}

// External sync — only rebuild when the incoming set differs from what we'd
// emit, so the parent's reactive roundtrip doesn't clobber an in-progress edit.
watch(
  () => props.domains,
  (next) => {
    if (JSON.stringify(next) === JSON.stringify(emittedDomains())) return
    rows.value = next.map((v) => ({ id: newId(), value: v }))
    primaryId.value = resolvePrimaryId(props.primary)
  },
  { deep: true },
)
watch(
  () => props.primary,
  (next) => {
    if (next.trim() === emittedPrimary().trim()) return
    primaryId.value = resolvePrimaryId(next)
  },
)

function addRow() {
  const row = { id: newId(), value: '' }
  rows.value = [...rows.value, row]
  // An empty row doesn't count until the user types into it. Start editing it
  // immediately so Add behaves like inserting a row in the other form grids.
  nextTick(() => {
    const rowIndex = rows.value.findIndex((candidate) => candidate.id === row.id)
    builder.api?.ensureIndexVisible(rowIndex)
    builder.api?.startEditingCell({ rowIndex, colKey: 'value' })
  })
}

function removeRow(id: string) {
  rows.value = rows.value.filter((r) => r.id !== id)
  emitAll()
}

function setPrimary(row: Row) {
  if (props.disabled || !row.value.trim()) return
  primaryId.value = row.id
  emitAll()
}

const builder = CoarGridBuilder.create<Row>()
  .rowDataRef(rows)
  .option('getRowId', (p: any) => p.data.id)
  .option('headerHeight', 0)
  .option('loading', false)
  .option('suppressNoRowsOverlay', true)
  .option('singleClickEdit', true)
  .stopEditingWhenCellsLoseFocus(true)
  .columns([
    (col) =>
      col
        .wrap(
          col
            .text('value', (c) => c.placeholder(props.placeholder))
            .editable(!props.disabled)
            .flex(1),
        )
        .right([
          {
            icon: (row) => row.id === primaryId.value ? 'circle-check' : 'circle',
            size: 's',
            color: (row) => row.id === primaryId.value
              ? 'var(--coar-text-brand-primary, #009fe3)'
              : 'var(--coar-text-neutral-tertiary, #9ca3af)',
            tooltip: (row) => row.id === primaryId.value
              ? t('admin.realms.primaryBadge', {}, 'Primary')
              : t('admin.realms.makePrimary', {}, 'Make primary domain'),
            show: (row) => !!row.value.trim(),
            onClick: (row) => setPrimary(row),
          },
          {
            icon: 'trash-2',
            size: 's',
            color: 'var(--coar-text-neutral-secondary, #9ca3af)',
            tooltip: t('common.delete', {}, 'Delete'),
            show: () => !props.disabled,
            onClick: (row) => removeRow(row.id),
          },
        ]),
  ])
  .onCellValueChanged(() => emitAll())
</script>

<template>
  <div class="realm-domains-field">
    <CoarDataGrid :builder="builder" bordered>
      <template #toolbar-left>
        <span class="realm-domains-title">{{ t('admin.realms.domains', {}, 'Domains') }}</span>
      </template>
      <template #toolbar-right>
        <CoarButton v-if="!disabled" size="s" icon-start="plus" variant="ghost" @click="addRow">
          {{ t('admin.realms.addDomain', {}, 'Add domain') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

  </div>
</template>

<style scoped>
.realm-domains-field {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 16rem;
}

.realm-domains-field :deep(.ag-theme-cocoar--bordered) {
  flex: 1;
  min-height: 0;
}

.realm-domains-field :deep(.ag-theme-cocoar--bordered > .coar-grid-toolbar) {
  min-height: 2.75rem;
  margin: 0;
  padding: 0.35rem 0.6rem;
}

.realm-domains-title {
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

</style>
