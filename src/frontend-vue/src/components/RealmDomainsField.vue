<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { CoarTextInput, CoarRadioGroup, CoarRadioButton, CoarButton, CoarTag } from '@cocoar/vue-ui'
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
 * <para>Deliberately plain flex markup, not the shared AG-Grid
 * <c>EditableStringList</c>: (a) realm-specific primary semantics don't belong
 * in that generic component, and (b) it sidesteps the AG-Grid auto-height
 * pitfalls. Primary is tracked by row <i>id</i>, not by value, so renaming the
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

// CoarRadioGroup binds to the primary row id.
const selectedPrimary = computed<string>({
  get: () => primaryId.value,
  set: (id) => {
    primaryId.value = id
    emitAll()
  },
})

function onRowInput(row: Row, value: string) {
  row.value = value
  emitAll()
}

function addRow() {
  rows.value = [...rows.value, { id: newId(), value: '' }]
  // No emit yet — an empty row doesn't count until the user types into it.
}

function removeRow(id: string) {
  rows.value = rows.value.filter((r) => r.id !== id)
  emitAll()
}

// Empty-state guidance: keyed off whether any *filled* domain exists, not the
// raw row count — so blanking the sole row (clear button) still shows the hint.
const isEmpty = computed(() => emittedDomains().length === 0)
</script>

<template>
  <div class="flex flex-col gap-2">
    <CoarRadioGroup
      name="realm-primary-domain"
      :model-value="selectedPrimary"
      :disabled="disabled"
      orientation="vertical"
      @update:model-value="(v: unknown) => (selectedPrimary = v as string)"
    >
      <div v-for="row in rows" :key="row.id" class="flex items-center gap-2">
        <CoarRadioButton
          :value="row.id"
          :disabled="disabled || !row.value.trim()"
        >
          <!-- Per-option accessible name: the radio's :value is an opaque row id,
               so screen readers need the domain text spelled out (visually hidden). -->
          <span class="sr-only">
            {{ row.value.trim() || t('admin.realms.newDomainRadioLabel', {}, 'New domain') }}
          </span>
        </CoarRadioButton>
        <CoarTextInput
          class="flex-1"
          :model-value="row.value"
          :placeholder="placeholder"
          :disabled="disabled"
          clearable
          @update:model-value="(v: string) => onRowInput(row, v)"
        />
        <CoarTag
          v-if="row.id === primaryId && row.value.trim()"
          variant="accent"
          size="s"
        >
          {{ t('admin.realms.primaryBadge', {}, 'Primary') }}
        </CoarTag>
        <CoarButton
          v-if="!disabled"
          variant="ghost"
          size="s"
          icon-start="trash-2"
          :title="t('common.delete', {}, 'Delete')"
          @click="removeRow(row.id)"
        />
      </div>
    </CoarRadioGroup>

    <p v-if="isEmpty" class="text-xs text-gray-500">
      {{ t('admin.realms.domainsEmpty', {}, 'No domains yet — add at least one. The first you add becomes the primary.') }}
    </p>

    <div v-if="!disabled">
      <CoarButton size="s" icon-start="plus" variant="ghost" @click="addRow">
        {{ t('admin.realms.addDomain', {}, 'Add domain') }}
      </CoarButton>
    </div>
  </div>
</template>
