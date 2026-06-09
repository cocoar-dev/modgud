<script setup lang="ts">
import { CoarCard, CoarIcon, CoarSpinner } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import type { KpiTile, TileTone } from './kpiTile'

defineProps<{ tile: KpiTile }>()

const { t } = useI18n()

// Tile-icon tone → scoped CSS class. The actual rgba(...0.15) palette lives in
// the <style scoped> block below.
const TONE_CLASS: Record<TileTone, string> = {
  rose: 'kpi-icon--red',
  amber: 'kpi-icon--orange',
  emerald: 'kpi-icon--green',
  sky: 'kpi-icon--blue',
  violet: 'kpi-icon--purple',
  blue: 'kpi-icon--blue',
}
</script>

<template>
  <CoarCard
    elevated
    variant="info"
    class="kpi-card"
    :role="tile.onClick ? 'button' : undefined"
    :tabindex="tile.onClick ? 0 : undefined"
    @click="tile.onClick && tile.onClick()"
    @keydown.enter.space.prevent="tile.onClick && tile.onClick()"
  >
    <!-- Drill-down affordance: the tile already navigates on click; the chevron
         (revealed on hover/focus) + keyboard support make that discoverable and
         accessible (UI/UX wave 4, #14). -->
    <CoarIcon v-if="tile.onClick" name="arrow-right" size="s" class="kpi-arrow" />
    <div class="kpi-content">
      <div class="kpi-icon" :class="TONE_CLASS[tile.tone]">
        <CoarIcon :name="tile.icon" size="m" />
      </div>
      <div
        class="kpi-value"
        :class="{
          'kpi-value--bad': tile.bad,
          'kpi-value--warn': tile.warn,
        }"
      >
        <CoarSpinner v-if="tile.loading" size="s" />
        <template v-else>
          <!-- Pair the bad/warn colour with an icon so status isn't signalled by
               colour alone (UI/UX wave 4, P2). -->
          <CoarIcon v-if="tile.bad" name="alert-triangle" size="s" class="kpi-status-icon" aria-hidden="true" />
          <CoarIcon v-else-if="tile.warn" name="alert-circle" size="s" class="kpi-status-icon" aria-hidden="true" />
          <span v-if="tile.bad || tile.warn" class="sr-only">{{ t('dashboard.kpi.attention', {}, 'Achtung') }}</span>
          {{ tile.value ?? '–' }}
        </template>
      </div>
      <div class="kpi-label">{{ tile.caption }}</div>
    </div>
  </CoarCard>
</template>

<style scoped>
/* KPI cards: lifted-on-hover style. */
.kpi-card {
  position: relative;
  cursor: pointer;
  transition: transform 0.2s ease, box-shadow 0.2s ease;
}
.kpi-card:hover {
  transform: translateY(-3px);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.15);
}
.kpi-card:focus-visible {
  outline: 2px solid var(--coar-accent, #1077be);
  outline-offset: 2px;
}

/* Drill-down chevron — hidden until the tile is hovered or keyboard-focused. */
.kpi-arrow {
  position: absolute;
  top: 0.5rem;
  right: 0.5rem;
  opacity: 0;
  transition: opacity 0.15s ease;
  color: var(--coar-text-neutral-secondary, #9ca3af);
}
.kpi-card:hover .kpi-arrow,
.kpi-card:focus-visible .kpi-arrow {
  opacity: 1;
}

/* Status icon paired with the bad/warn value colour (inherits currentColor). */
.kpi-status-icon {
  margin-right: 0.25rem;
}

.kpi-content {
  display: flex;
  flex-direction: column;
  align-items: center;
  padding: 1rem 0.5rem;
  gap: 0.25rem;
}

.kpi-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 2.5rem;
  height: 2.5rem;
  border-radius: 0.75rem;
  margin-bottom: 0.25rem;
}

.kpi-icon--blue   { background: rgba(59, 130, 246, 0.15); color: #3b82f6; }
.kpi-icon--red    { background: rgba(239, 68, 68, 0.15);  color: #ef4444; }
.kpi-icon--orange { background: rgba(245, 158, 11, 0.15); color: #f59e0b; }
.kpi-icon--purple { background: rgba(139, 92, 246, 0.15); color: #8b5cf6; }
.kpi-icon--green  { background: rgba(34, 197, 94, 0.15);  color: #22c55e; }

.kpi-value {
  font-size: 1.75rem;
  font-weight: 700;
  line-height: 1;
  min-height: 1.75rem;
  display: flex;
  align-items: center;
}
.kpi-value--bad  { color: #ef4444; }
.kpi-value--warn { color: #f59e0b; }

.kpi-label {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  text-align: center;
}
</style>
