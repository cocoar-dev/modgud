<script setup lang="ts">
import { computed } from 'vue'
import { CoarTag, vTooltip } from '@cocoar/vue-ui'

const props = defineProps<{
  params: any
}>()

interface Responsible {
  acronym: string
  fullName: string
}

function mapRef(r: any): Responsible {
  const label = r.Label ?? ''
  const parts = label.split(' | ')
  return {
    acronym: parts[0] || label,
    fullName: parts[1] || label,
  }
}

const responsibles = computed<Responsible[]>(() => {
  const value = props.params.value
  if (!value) return []
  if (Array.isArray(value)) {
    return value.map(mapRef).sort((a, b) => a.acronym.localeCompare(b.acronym))
  }
  // Single RefPropertyDto (e.g. CreatedBy)
  return [mapRef(value)]
})
</script>

<template>
  <div class="flex items-center gap-1 overflow-hidden h-full">
    <CoarTag
      v-for="r in responsibles"
      :key="r.acronym"
      v-tooltip="r.fullName"
      size="s"
    >
      {{ r.acronym }}
    </CoarTag>
  </div>
</template>
