<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import { useDashboardStats } from '../dashboardStats'
import DonutChart, { type DonutSegment } from '../charts/DonutChart.vue'
import WidgetCard from './WidgetCard.vue'

const { t, language } = useI18n()
const { stats, loading, failed } = useDashboardStats()

// Colour follows the method, never its rank — a method keeps its colour whether
// or not the others occur. Codes are ModgudMeters.LoginMethod on the backend.
const METHODS: Record<string, { color: string; en: string }> = {
  password: { color: 'var(--chart-series-1)', en: 'Password' },
  passkey: { color: 'var(--chart-series-2)', en: 'Passkey' },
  external: { color: 'var(--chart-series-3)', en: 'External provider' },
  magic_link: { color: 'var(--chart-series-4)', en: 'Login link' },
  email_otp: { color: 'var(--chart-series-5)', en: 'E-mail code' },
  mfa: { color: 'var(--chart-series-6)', en: 'With second factor' },
}

const logins = computed(() => stats.value?.Logins ?? null)

const segments = computed<DonutSegment[]>(() => {
  const known: DonutSegment[] = []
  let other = 0
  for (const { Method, Count } of logins.value?.Methods ?? []) {
    const spec = METHODS[Method]
    if (!spec) { other += Count; continue }
    known.push({
      key: Method,
      label: t(`dashboard.loginMethods.method.${Method}`, {}, spec.en),
      value: Count,
      color: spec.color,
    })
  }
  if (other > 0) {
    known.push({
      key: 'other',
      label: t('dashboard.loginMethods.method.other', {}, 'Other'),
      value: other,
      color: 'var(--chart-series-other)',
    })
  }
  return known
})

const title = computed(() => t('dashboard.loginMethods.title', {}, 'Sign-in methods'))
</script>

<template>
  <WidgetCard
    :title="title"
    icon="key-round"
    :subtitle="logins ? t('dashboard.loginMethods.subtitle', { n: logins.Days }, 'Successful sign-ins, last {n} days') : undefined"
    :loading="loading && !stats"
    :failed="failed"
    :empty="logins && segments.length === 0 ? t('dashboard.loginMethods.none', {}, 'No sign-ins in this period.') : null"
  >
    <DonutChart
      v-if="logins"
      :segments="segments"
      :total-label="t('dashboard.loginMethods.total', {}, 'sign-ins')"
      :locale="language"
      :label="title"
    />
  </WidgetCard>
</template>
