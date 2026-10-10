<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import './chart-theme.css'

/** One line over the shared day axis. `color` is a CSS colour (use a --chart-* variable). */
export interface TrendSeries {
  key: string
  label: string
  color: string
  values: number[]
  /** Lays a light wash under the line — for the series the chart is about. */
  area?: boolean
}

const props = defineProps<{
  /** ISO days (yyyy-MM-dd), oldest first; one entry per value. */
  days: string[]
  series: TrendSeries[]
  /** BCP-47 tag for dates and numbers. */
  locale: string
  /** Accessible name of the chart. */
  label: string
}>()

const HEIGHT = 200
const PAD = { top: 10, right: 12, bottom: 22, left: 36 }

// Drawn at the element's real pixel width so text and 2px lines are never scaled.
const root = ref<HTMLElement | null>(null)
const width = ref(600)
let observer: ResizeObserver | null = null
onMounted(() => {
  if (!root.value) return
  width.value = root.value.clientWidth || width.value
  observer = new ResizeObserver((entries) => { width.value = entries[0]?.contentRect.width || width.value })
  observer.observe(root.value)
})
onBeforeUnmount(() => observer?.disconnect())

const plotW = computed(() => Math.max(1, width.value - PAD.left - PAD.right))
const plotH = HEIGHT - PAD.top - PAD.bottom

/** A round axis maximum (1, 2 or 5 × a power of ten, at least 4 so ticks stay whole). */
const yMax = computed(() => {
  const max = Math.max(0, ...props.series.flatMap(s => s.values))
  if (max <= 4) return 4
  const magnitude = 10 ** Math.floor(Math.log10(max))
  return [1, 2, 4, 6, 8, 10].map(m => m * magnitude).find(step => step >= max) ?? max
})

const numberFormat = computed(() => new Intl.NumberFormat(props.locale))
const yTicks = computed(() => [0, yMax.value / 2, yMax.value].map(value => ({
  value,
  label: numberFormat.value.format(value),
  y: y(value),
})))

function x(index: number): number {
  const last = Math.max(1, props.days.length - 1)
  return PAD.left + (index / last) * plotW.value
}
function y(value: number): number {
  return PAD.top + plotH - (value / yMax.value) * plotH
}

const paths = computed(() => props.series.map(s => {
  const points = s.values.map((v, i) => `${x(i).toFixed(1)},${y(v).toFixed(1)}`)
  const line = `M${points.join('L')}`
  const baseline = y(0).toFixed(1)
  return {
    ...s,
    line,
    fill: s.area && points.length > 1
      ? `${line}L${x(s.values.length - 1).toFixed(1)},${baseline}L${x(0).toFixed(1)},${baseline}Z`
      : null,
  }
}))

function parseDay(day: string): Date {
  const [yy = 0, mm = 1, dd = 1] = day.split('-').map(Number)
  return new Date(yy, mm - 1, dd)
}
const shortDate = computed(() => new Intl.DateTimeFormat(props.locale, { day: 'numeric', month: 'short' }))
const longDate = computed(() => new Intl.DateTimeFormat(props.locale, { weekday: 'short', day: 'numeric', month: 'long' }))

/** First, middle and last day — enough to orient without crowding the axis. */
const xTicks = computed(() => {
  const n = props.days.length
  if (n === 0) return []
  const picks = n < 3 ? [0, n - 1] : [0, Math.floor((n - 1) / 2), n - 1]
  return [...new Set(picks)].map((i, pos, all) => ({
    x: x(i),
    label: shortDate.value.format(parseDay(props.days[i]!)),
    anchor: pos === 0 ? 'start' : pos === all.length - 1 ? 'end' : 'middle',
  }))
})

// ── Hover: a crosshair snaps to the nearest day; the reader aims at a date,
//    never at a 2px line. ─────────────────────────────────────────────────
const hover = ref<number | null>(null)

function onPointerMove(event: PointerEvent) {
  if (!root.value || props.days.length === 0) return
  const offset = event.clientX - root.value.getBoundingClientRect().left - PAD.left
  const last = props.days.length - 1
  hover.value = Math.min(last, Math.max(0, Math.round((offset / plotW.value) * last)))
}

function onKeydown(event: KeyboardEvent) {
  const last = props.days.length - 1
  if (last < 0) return
  if (event.key === 'ArrowLeft') hover.value = Math.max(0, (hover.value ?? last) - 1)
  else if (event.key === 'ArrowRight') hover.value = Math.min(last, (hover.value ?? -1) + 1)
  else if (event.key === 'Escape') hover.value = null
  else return
  event.preventDefault()
}

const tooltip = computed(() => {
  const i = hover.value
  const day = i === null ? undefined : props.days[i]
  if (i === null || day === undefined) return null
  const px = x(i)
  return {
    x: px,
    // Flip to the other side of the crosshair before it would leave the card.
    flip: px > width.value / 2,
    date: longDate.value.format(parseDay(day)),
    rows: props.series.map(s => ({
      key: s.key,
      label: s.label,
      color: s.color,
      value: numberFormat.value.format(s.values[i] ?? 0),
      y: y(s.values[i] ?? 0),
    })),
  }
})
</script>

<template>
  <div class="dash-chart trend">
    <div v-if="series.length > 1" class="dash-chart__legend">
      <span v-for="s in series" :key="s.key" class="dash-chart__legend-item">
        <span class="dash-chart__swatch" :style="{ background: s.color }" />
        {{ s.label }}
      </span>
    </div>

    <div
      ref="root"
      class="trend__plot"
      tabindex="0"
      role="img"
      :aria-label="label"
      @pointermove="onPointerMove"
      @pointerleave="hover = null"
      @blur="hover = null"
      @keydown="onKeydown"
    >
      <svg :width="width" :height="HEIGHT" :viewBox="`0 0 ${width} ${HEIGHT}`" aria-hidden="true">
        <g v-for="tick in yTicks" :key="tick.value">
          <line
            :x1="PAD.left" :x2="width - PAD.right" :y1="tick.y" :y2="tick.y"
            :class="tick.value === 0 ? 'trend__axis' : 'trend__grid'"
          />
          <text :x="PAD.left - 8" :y="tick.y" class="trend__tick" text-anchor="end" dominant-baseline="middle">
            {{ tick.label }}
          </text>
        </g>
        <text
          v-for="tick in xTicks" :key="tick.x"
          :x="tick.x" :y="HEIGHT - 5" class="trend__tick" :text-anchor="tick.anchor"
        >{{ tick.label }}</text>

        <template v-for="p in paths" :key="p.key">
          <path v-if="p.fill" :d="p.fill" :fill="p.color" fill-opacity="0.1" />
          <path :d="p.line" :stroke="p.color" class="trend__line" />
        </template>

        <g v-if="tooltip">
          <line :x1="tooltip.x" :x2="tooltip.x" :y1="PAD.top" :y2="PAD.top + plotH" class="trend__crosshair" />
          <circle
            v-for="row in tooltip.rows" :key="row.key"
            :cx="tooltip.x" :cy="row.y" r="4" :fill="row.color" class="trend__dot"
          />
        </g>
      </svg>

      <div
        v-if="tooltip"
        class="trend__tooltip"
        :class="{ 'trend__tooltip--flip': tooltip.flip }"
        :style="{ left: `${tooltip.x}px` }"
      >
        <div class="trend__tooltip-date">{{ tooltip.date }}</div>
        <div v-for="row in tooltip.rows" :key="row.key" class="trend__tooltip-row">
          <span class="dash-chart__swatch" :style="{ background: row.color }" />
          <span class="trend__tooltip-label">{{ row.label }}</span>
          <span class="trend__tooltip-value">{{ row.value }}</span>
        </div>
      </div>
    </div>

    <table class="dash-chart__table">
      <caption>{{ label }}</caption>
      <thead>
        <tr>
          <th scope="col" />
          <th v-for="s in series" :key="s.key" scope="col">{{ s.label }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(day, i) in days" :key="day">
          <th scope="row">{{ day }}</th>
          <td v-for="s in series" :key="s.key">{{ s.values[i] ?? 0 }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.trend {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  position: relative;
}
.trend__plot {
  position: relative;
  width: 100%;
  min-width: 0;
  border-radius: 0.25rem;
  touch-action: pan-y;
}
.trend__plot:focus-visible {
  outline: 2px solid var(--coar-accent, #1077be);
  outline-offset: 2px;
}
.trend__plot svg {
  display: block;
}

.trend__grid { stroke: var(--chart-grid); stroke-width: 1; }
.trend__axis { stroke: var(--chart-axis); stroke-width: 1; }
.trend__tick {
  fill: var(--chart-muted);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}
.trend__line {
  fill: none;
  stroke-width: 2;
  stroke-linejoin: round;
  stroke-linecap: round;
}
.trend__crosshair { stroke: var(--chart-axis); stroke-width: 1; }
/* The ring in the surface colour keeps a marker legible where lines cross. */
.trend__dot { stroke: var(--chart-surface); stroke-width: 2; }

.trend__tooltip {
  position: absolute;
  top: 0;
  transform: translateX(12px);
  pointer-events: none;
  z-index: 1;
  min-width: 9rem;
  padding: 0.5rem 0.625rem;
  border-radius: 0.375rem;
  border: 1px solid var(--coar-border-neutral-tertiary, rgba(11, 11, 11, 0.1));
  background: var(--chart-surface);
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.12);
  font-size: 0.75rem;
  color: var(--chart-ink);
}
.trend__tooltip--flip {
  transform: translateX(calc(-100% - 12px));
}
.trend__tooltip-date {
  margin-bottom: 0.25rem;
  color: var(--chart-ink-secondary);
  white-space: nowrap;
}
.trend__tooltip-row {
  display: flex;
  align-items: center;
  gap: 0.375rem;
  white-space: nowrap;
}
.trend__tooltip-label { flex: 1; }
.trend__tooltip-value {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  margin-left: 0.75rem;
}
</style>
