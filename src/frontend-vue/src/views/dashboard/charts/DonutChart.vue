<script setup lang="ts">
import { computed, ref } from 'vue'
import './chart-theme.css'

/** One slice. `color` is a CSS colour (use a --chart-* variable). */
export interface DonutSegment {
  key: string
  label: string
  value: number
  color: string
}

const props = defineProps<{
  segments: DonutSegment[]
  /** Caption under the total in the middle of the ring. */
  totalLabel: string
  /** BCP-47 tag for numbers. */
  locale: string
  /** Accessible name of the chart. */
  label: string
}>()

const SIZE = 132
const STROKE = 16
const RADIUS = (SIZE - STROKE) / 2
const CIRCUMFERENCE = 2 * Math.PI * RADIUS
// Touching slices are separated by a gap in the surface colour, not a border.
const GAP = 2

const numberFormat = computed(() => new Intl.NumberFormat(props.locale))
const total = computed(() => props.segments.reduce((sum, s) => sum + s.value, 0))

const arcs = computed(() => {
  const visible = props.segments.filter(s => s.value > 0)
  const gap = visible.length > 1 ? GAP : 0
  let offset = 0
  return visible.map(s => {
    const length = (s.value / total.value) * CIRCUMFERENCE
    const arc = {
      ...s,
      dash: `${Math.max(0.5, length - gap)} ${CIRCUMFERENCE}`,
      offset: -offset,
    }
    offset += length
    return arc
  })
})

const rows = computed(() => props.segments.map(s => ({
  ...s,
  count: numberFormat.value.format(s.value),
  share: total.value > 0 ? `${Math.round((s.value / total.value) * 100)} %` : '–',
})))

const active = ref<string | null>(null)
</script>

<template>
  <div class="dash-chart donut">
    <div class="donut__ring" role="img" :aria-label="label">
      <svg :width="SIZE" :height="SIZE" :viewBox="`0 0 ${SIZE} ${SIZE}`" aria-hidden="true">
        <circle :cx="SIZE / 2" :cy="SIZE / 2" :r="RADIUS" class="donut__track" :stroke-width="STROKE" />
        <g :transform="`rotate(-90 ${SIZE / 2} ${SIZE / 2})`">
          <circle
            v-for="arc in arcs" :key="arc.key"
            :cx="SIZE / 2" :cy="SIZE / 2" :r="RADIUS"
            class="donut__arc"
            :class="{ 'donut__arc--dim': active !== null && active !== arc.key }"
            :stroke="arc.color" :stroke-width="STROKE"
            :stroke-dasharray="arc.dash" :stroke-dashoffset="arc.offset"
            @pointerenter="active = arc.key"
            @pointerleave="active = null"
          />
        </g>
      </svg>
      <div class="donut__center">
        <div class="donut__total">{{ numberFormat.format(total) }}</div>
        <div class="donut__total-label">{{ totalLabel }}</div>
      </div>
    </div>

    <!-- The legend carries every value, so nothing is readable by colour or hover alone. -->
    <ul class="donut__legend">
      <li
        v-for="row in rows" :key="row.key"
        class="donut__row"
        :class="{ 'donut__row--active': active === row.key }"
        @pointerenter="active = row.key"
        @pointerleave="active = null"
      >
        <span class="dash-chart__swatch" :style="{ background: row.color }" />
        <span class="donut__row-label">
          <slot name="label" :segment="row">{{ row.label }}</slot>
        </span>
        <span class="donut__row-value">{{ row.count }}</span>
        <span class="donut__row-share">{{ row.share }}</span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.donut {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: center;
  gap: 1rem;
}
.donut__ring {
  position: relative;
  flex-shrink: 0;
  line-height: 0;
}
.donut__track {
  fill: none;
  stroke: var(--chart-grid);
}
.donut__arc {
  fill: none;
  transition: opacity 0.12s ease;
}
.donut__arc--dim { opacity: 0.35; }

.donut__center {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  line-height: 1.1;
  pointer-events: none;
}
.donut__total {
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--chart-ink);
}
.donut__total-label {
  font-size: 0.6875rem;
  color: var(--chart-ink-secondary);
}

.donut__legend {
  flex: 1 1 15rem;
  min-width: 0;
  margin: 0;
  padding: 0;
  list-style: none;
  font-size: 0.8125rem;
}
.donut__row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.25rem 0.375rem;
  border-radius: 0.25rem;
  color: var(--chart-ink);
}
.donut__row--active {
  background: var(--coar-background-neutral-tertiary, rgba(0, 0, 0, 0.04));
}
.donut__row-label {
  flex: 1;
  min-width: 0;
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.donut__row-value {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.donut__row-share {
  width: 2.25rem;
  text-align: right;
  color: var(--chart-ink-secondary);
  font-variant-numeric: tabular-nums;
}
</style>
