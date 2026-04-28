<script setup lang="ts">
import type { ITooltipParams } from 'ag-grid-community'
import { CoarCard } from '@cocoar/vue-ui'
import DOMPurify from 'dompurify'
import { computed } from 'vue'

const props = defineProps<{
  params: ITooltipParams
}>()

const sanitizedHtml = computed(() => {
  const raw = props.params.value
  if (!raw) return ''
  return DOMPurify.sanitize(String(raw))
})
</script>

<template>
  <CoarCard v-if="sanitizedHtml" elevated class="html-tooltip" style="background: var(--coar-background-neutral-primary)">
    <div class="p-3" v-html="sanitizedHtml" />
  </CoarCard>
</template>

<style>
.html-tooltip {
  max-width: 600px;
  max-height: 300px;
  overflow: auto;
  font-size: 0.85em;
  line-height: 1.5;
}
</style>
