<script setup lang="ts">
import { computed } from 'vue'
import { CoarFormField, CoarTextInput, CoarCheckbox, CoarSelect } from '@cocoar/vue-ui'
import type { FlavorConfigFieldDto } from '@/models/loginProvider'

const props = withDefaults(defineProps<{
  schema: FlavorConfigFieldDto[]
  modelValue: Record<string, unknown>
  /** Only render fields belonging to this section (default 'connection'). */
  section?: string
}>(), { section: 'connection' })

const emit = defineEmits<{
  (e: 'update:modelValue', value: Record<string, unknown>): void
}>()

// Fields whose Section matches this panel. A missing Section means 'connection'
// (backwards-compatible with OIDC flavors that predate sections).
const fields = computed(() =>
  props.schema.filter((f) => (f.Section ?? 'connection') === props.section),
)

function selectOptions(field: FlavorConfigFieldDto) {
  return (field.Options ?? []).map((o) => ({ value: o.Value, label: o.Label }))
}

function update(key: string, value: unknown, current: Record<string, unknown>) {
  emit('update:modelValue', { ...current, [key]: value })
}
</script>

<template>
  <div class="flex flex-col gap-2">
    <p v-if="fields.length === 0" class="help-text">—</p>
    <template v-for="field in fields" :key="field.Key">
      <CoarFormField :label="field.Label">
        <textarea
          v-if="field.Type === 'MultilineText'"
          class="multiline-input"
          rows="6"
          :placeholder="field.Placeholder ?? ''"
          :value="(modelValue[field.Key] as string) ?? ''"
          @input="(e: Event) => update(field.Key, (e.target as HTMLTextAreaElement).value, modelValue)"
        ></textarea>
        <CoarTextInput
          v-else-if="field.Type === 'String' || field.Type === 'Url'"
          :model-value="(modelValue[field.Key] as string) ?? ''"
          :placeholder="field.Placeholder ?? ''"
          clearable
          @update:model-value="(v: string) => update(field.Key, v, modelValue)"
        />
        <CoarCheckbox
          v-else-if="field.Type === 'Boolean'"
          :model-value="!!modelValue[field.Key]"
          @update:model-value="(v: boolean) => update(field.Key, v, modelValue)"
        />
        <CoarSelect
          v-else-if="field.Type === 'Select'"
          :model-value="modelValue[field.Key] == null ? '' : String(modelValue[field.Key])"
          :options="selectOptions(field)"
          @update:model-value="(v: string | null) => update(field.Key, v ?? '', modelValue)"
        />
        <CoarTextInput
          v-else
          :model-value="(modelValue[field.Key] as string) ?? ''"
          :placeholder="field.Placeholder ?? ''"
          clearable
          @update:model-value="(v: string) => update(field.Key, v, modelValue)"
        />
      </CoarFormField>
      <div v-if="field.HelpText" class="help-text">{{ field.HelpText }}</div>
    </template>
  </div>
</template>

<style scoped>
.help-text {
  font-size: 0.8rem;
  color: #6b7280;
  margin-top: -6px;
  margin-bottom: 4px;
}

.multiline-input {
  width: 100%;
  padding: 8px 10px;
  font-family: monospace;
  font-size: 0.85rem;
  border: 1px solid var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: 4px;
  background: var(--coar-background-neutral-primary, #fff);
  color: inherit;
  resize: vertical;
}
</style>
