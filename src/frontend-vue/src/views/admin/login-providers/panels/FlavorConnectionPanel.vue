<script setup lang="ts">
import { CoarFormField, CoarTextInput, CoarCheckbox } from '@cocoar/vue-ui'
import type { FlavorConfigFieldDto } from '@/models/loginProvider'

defineProps<{
  schema: FlavorConfigFieldDto[]
  modelValue: Record<string, unknown>
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: Record<string, unknown>): void
}>()

function update(key: string, value: unknown, current: Record<string, unknown>) {
  emit('update:modelValue', { ...current, [key]: value })
}
</script>

<template>
  <div class="flex flex-col gap-2">
    <template v-for="field in schema" :key="field.Key">
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
