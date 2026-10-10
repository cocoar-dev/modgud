<script setup lang="ts">
// An (i) icon that shows an explanation on hover or keyboard focus — for static help
// text that would otherwise sit as a notice in the form. Warnings and state messages
// stay visible notices; only explanations belong here.
import { CoarIcon, vTooltip } from '@cocoar/vue-ui'

defineProps<{ text: string }>()
</script>

<template>
  <span
    class="info-tip"
    tabindex="0"
    role="img"
    :aria-label="text"
    v-tooltip="{ content: text, placement: 'top' }">
    <CoarIcon name="info" size="s" aria-hidden="true" />
  </span>
</template>

<style scoped>
.info-tip {
  display: inline-flex;
  align-items: center;
  flex-shrink: 0;
  width: 1rem;
  height: 1rem;
  color: var(--coar-text-neutral-tertiary, #6b7280);
  cursor: help;
}
.info-tip:focus-visible {
  outline: 2px solid var(--coar-border-focus, #2563eb);
  outline-offset: 2px;
  border-radius: 50%;
}
</style>
