<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarIcon } from '@cocoar/vue-ui'
import { useDashboardStats } from '../dashboardStats'
import DonutChart, { type DonutSegment } from '../charts/DonutChart.vue'
import WidgetCard from './WidgetCard.vue'

const { t, language } = useI18n()
const router = useRouter()
const { stats, loading, failed } = useDashboardStats()

const security = computed(() => stats.value?.Security ?? null)
const numberFormat = computed(() => new Intl.NumberFormat(language.value))

// Severity is a state, so it wears the reserved status colours — and an icon,
// because colour alone must not carry it.
const ICONS: Record<string, string> = { error: 'x-circle', warning: 'triangle-alert', info: 'info' }

const segments = computed<DonutSegment[]>(() => {
  const s = security.value
  if (!s) return []
  return [
    { key: 'error', label: t('dashboard.securityEvents.error', {}, 'Errors'), value: s.Error, color: 'var(--chart-status-critical)' },
    { key: 'warning', label: t('dashboard.securityEvents.warning', {}, 'Warnings'), value: s.Warning, color: 'var(--chart-status-warning)' },
    { key: 'info', label: t('dashboard.securityEvents.info', {}, 'Informational'), value: s.Info, color: 'var(--chart-status-neutral)' },
  ]
})

const total = computed(() => segments.value.reduce((sum, s) => sum + s.value, 0))

/** "security.login_failed_unknown_user" → "Login failed unknown user", unless translated. */
function eventLabel(code: string): string {
  const words = code.slice(code.indexOf('.') + 1).replace(/_/g, ' ')
  return t(`dashboard.securityEvents.type.${code}`, {}, words.charAt(0).toUpperCase() + words.slice(1))
}

const title = computed(() => t('dashboard.securityEvents.title', {}, 'Security events'))
</script>

<template>
  <WidgetCard
    :title="title"
    icon="shield-alert"
    :subtitle="security ? t('dashboard.securityEvents.subtitle', { n: security.Days }, 'Last {n} days') : undefined"
    :loading="loading && !stats"
    :failed="failed"
    :empty="security && total === 0 ? t('dashboard.securityEvents.none', {}, 'No security events in this period.') : null"
    :cta="t('dashboard.securityEvents.cta', {}, 'Open security log →')"
    @open="router.push('/admin/logs')"
  >
    <template v-if="security">
      <DonutChart
        :segments="segments"
        :total-label="t('dashboard.securityEvents.total', {}, 'events')"
        :locale="language"
        :label="title"
      >
        <template #label="{ segment }">
          <CoarIcon :name="ICONS[segment.key]" size="s" />
          {{ segment.label }}
        </template>
      </DonutChart>

      <div v-if="security.TopEventTypes.length" class="top-events">
        <div class="top-events__heading">
          {{ t('dashboard.securityEvents.top', {}, 'Most frequent warnings and errors') }}
        </div>
        <div v-for="event in security.TopEventTypes" :key="event.EventType" class="top-events__row">
          <span class="top-events__label">{{ eventLabel(event.EventType) }}</span>
          <span class="top-events__count">{{ numberFormat.format(event.Count) }}</span>
        </div>
      </div>
    </template>
  </WidgetCard>
</template>

<style scoped>
.top-events {
  margin-top: 1rem;
  padding-top: 0.75rem;
  border-top: 1px solid var(--coar-border-neutral-tertiary, rgba(0, 0, 0, 0.08));
  font-size: 0.8125rem;
}
.top-events__heading {
  margin-bottom: 0.25rem;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.top-events__row {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.1875rem 0;
}
.top-events__label {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.top-events__count {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
</style>
