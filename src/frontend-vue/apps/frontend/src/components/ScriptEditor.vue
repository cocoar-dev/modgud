<script setup lang="ts">
import { CoarScriptEditor } from '@cocoar/vue-script-editor'

defineProps<{
  modelValue: string
  placeholder?: string
  height?: string | number
  preamble?: string
  extraLibs?: { content: string; filePath: string }[]
  disabled?: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: string): void
}>()

function onUpdate(value: string) {
  emit('update:modelValue', value)
}
</script>

<template>
  <div class="script-editor-wrap">
    <CoarScriptEditor
      :model-value="modelValue"
      :placeholder="placeholder"
      :height="height ?? '180px'"
      :preamble="preamble"
      :extra-libs="extraLibs"
      :disabled="disabled"
      language="typescript"
      variant="inline"
      script-mode
      @update:model-value="onUpdate"
    />
  </div>
</template>

<style scoped>
.script-editor-wrap {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--coar-border-neutral-secondary, #e2e8f0);
  border-radius: var(--coar-radius-m, 4px);
  overflow: hidden;
  background: var(--coar-background-neutral-primary, white);
}
</style>
