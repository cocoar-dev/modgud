<script setup lang="ts">
/**
 * Structured editor for a SAML `key → string[]` map (AttributeMap: logical
 * claim name → IdP attribute URIs; AmrMapping: AuthnContextClassRef → AMR
 * values). Lets admins fine-tune the mapping when an IdP changes a claim URI,
 * instead of being stuck with the flavor's seeded defaults.
 *
 * Rows are the local editing source of truth; they re-initialise from
 * `modelValue` only when `reloadKey` changes (provider/flavor switch), which
 * avoids an update→reinit feedback loop while typing.
 */
import { ref, watch } from 'vue'
import { CoarTextInput, CoarButton } from '@cocoar/vue-ui'

const props = withDefaults(defineProps<{
  modelValue: Record<string, string[]> | undefined
  keyLabel: string
  valueLabel: string
  keyPlaceholder?: string
  valuePlaceholder?: string
  addLabel: string
  /** Re-init rows when this changes (e.g. provider id / flavor key). */
  reloadKey?: string
}>(), { keyPlaceholder: '', valuePlaceholder: '', reloadKey: '' })

const emit = defineEmits<{
  (e: 'update:modelValue', value: Record<string, string[]>): void
}>()

interface Row { key: string; values: string }

const rows = ref<Row[]>([])

function fromMap(m: Record<string, string[]> | undefined): Row[] {
  return Object.entries(m ?? {}).map(([key, values]) => ({
    key,
    values: (Array.isArray(values) ? values : []).join(', '),
  }))
}

function toMap(): Record<string, string[]> {
  const out: Record<string, string[]> = {}
  for (const r of rows.value) {
    const k = r.key.trim()
    if (!k) continue
    out[k] = r.values.split(',').map((s) => s.trim()).filter(Boolean)
  }
  return out
}

watch(
  () => props.reloadKey,
  () => { rows.value = fromMap(props.modelValue) },
  { immediate: true },
)

function emitChange() {
  emit('update:modelValue', toMap())
}

function addRow() {
  rows.value.push({ key: '', values: '' })
}

function removeRow(i: number) {
  rows.value.splice(i, 1)
  emitChange()
}
</script>

<template>
  <div class="claim-map">
    <div class="claim-map-head">
      <span class="claim-map-col-label">{{ keyLabel }}</span>
      <span class="claim-map-col-label">{{ valueLabel }}</span>
      <span class="claim-map-col-action"></span>
    </div>
    <div v-for="(row, i) in rows" :key="i" class="claim-map-row">
      <CoarTextInput
        v-model="row.key"
        :placeholder="keyPlaceholder"
        clearable
        @update:model-value="emitChange"
      />
      <CoarTextInput
        v-model="row.values"
        :placeholder="valuePlaceholder"
        clearable
        @update:model-value="emitChange"
      />
      <CoarButton size="s" variant="ghost" icon-start="trash-2" @click="removeRow(i)" />
    </div>
    <div>
      <CoarButton size="s" variant="ghost" icon-start="plus" @click="addRow">
        {{ addLabel }}
      </CoarButton>
    </div>
  </div>
</template>

<style scoped>
.claim-map {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.claim-map-head,
.claim-map-row {
  display: grid;
  grid-template-columns: 1fr 1.5fr auto;
  gap: 8px;
  align-items: center;
}
.claim-map-col-label {
  font-size: 0.8rem;
  color: #6b7280;
}
</style>
