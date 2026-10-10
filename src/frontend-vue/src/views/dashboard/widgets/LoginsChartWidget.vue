<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { useDashboardStats } from '../dashboardStats'
import TrendChart, { type TrendSeries } from '../charts/TrendChart.vue'
import WidgetCard from './WidgetCard.vue'

const { t, language } = useI18n()
const router = useRouter()
const { stats, loading, failed } = useDashboardStats()

const logins = computed(() => stats.value?.Logins ?? null)
const numberFormat = computed(() => new Intl.NumberFormat(language.value))

const title = computed(() => t('dashboard.logins.title', {}, 'Sign-ins'))

const succeededLabel = computed(() => t('dashboard.logins.succeeded', {}, 'Successful'))
const failedLabel = computed(() => t('dashboard.logins.failed', {}, 'Failed'))

const series = computed<TrendSeries[]>(() => [
  {
    key: 'succeeded',
    label: succeededLabel.value,
    color: 'var(--chart-series-1)',
    values: logins.value?.Series.map(d => d.Succeeded) ?? [],
    area: true,
  },
  {
    key: 'failed',
    label: failedLabel.value,
    color: 'var(--chart-series-2)',
    values: logins.value?.Series.map(d => d.Failed) ?? [],
  },
])
</script>

<template>
  <WidgetCard
    :title="title"
    icon="activity"
    :subtitle="logins ? t('dashboard.logins.subtitle', { n: logins.Days }, 'Known accounts, last {n} days') : undefined"
    :loading="loading && !stats"
    :failed="failed"
    :cta="t('dashboard.logins.cta', {}, 'Open audit log →')"
    @open="router.push('/admin/logs')"
  >
    <template v-if="logins" #aside>
      <div class="logins__totals">
        <span><strong>{{ numberFormat.format(logins.Succeeded) }}</strong> {{ succeededLabel }}</span>
        <span><strong>{{ numberFormat.format(logins.Failed) }}</strong> {{ failedLabel }}</span>
      </div>
    </template>

    <TrendChart
      v-if="logins"
      :days="logins.Series.map(d => d.Day)"
      :series="series"
      :locale="language"
      :label="title"
    />
  </WidgetCard>
</template>

<style scoped>
.logins__totals {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 0 1rem;
  font-size: 0.8125rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
}
.logins__totals strong {
  font-weight: 700;
  color: var(--coar-text-neutral-primary, #111827);
}
</style>
