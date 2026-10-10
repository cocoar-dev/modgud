<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { useDashboardStats } from '../dashboardStats'
import type { KpiTile, TileTone } from '../kpiTile'
import KpiCard from '../KpiCard.vue'

/** A single headline number from the dashboard statistics. */
export type StatKpiKind = 'activeSessions' | 'failedLogins' | 'securityAttention' | 'pendingRequests'

const props = defineProps<{ kind: StatKpiKind }>()

const { t, language } = useI18n()
const router = useRouter()
const { stats, loading, failed } = useDashboardStats()

interface Spec {
  icon: string
  tone: TileTone
  caption: string
  to: string
  value: number | null | undefined
  /** How a non-zero value reads; unset = just a number. */
  nonZero?: 'warn' | 'bad'
}

const spec = computed<Spec>(() => {
  switch (props.kind) {
    case 'activeSessions':
      return {
        icon: 'monitor', tone: 'sky', to: '/admin/users',
        caption: t('dashboard.kpi.realmSessions', {}, 'Active sessions in the realm'),
        value: stats.value?.Counts.ActiveSessions,
      }
    case 'failedLogins':
      return {
        icon: 'shield-alert', tone: 'rose', to: '/admin/logs', nonZero: 'bad',
        caption: t('dashboard.kpi.failedLast24h', {}, 'Failed sign-ins, 24 h'),
        value: stats.value?.Logins?.FailedLast24h,
      }
    case 'securityAttention':
      return {
        icon: 'triangle-alert', tone: 'amber', to: '/admin/logs', nonZero: 'warn',
        caption: t('dashboard.kpi.securityAttention', {}, 'Security events, 24 h'),
        value: stats.value?.Security?.AttentionLast24h,
      }
    default:
      return {
        icon: 'inbox', tone: 'violet', to: '/admin/change-requests', nonZero: 'warn',
        caption: t('dashboard.kpi.pendingChangeRequests', {}, 'Open requests'),
        value: stats.value?.Counts.PendingChangeRequests,
      }
  }
})

const numberFormat = computed(() => new Intl.NumberFormat(language.value))

const tile = computed<KpiTile>(() => {
  const s = spec.value
  const pending = loading.value && !stats.value
  const value = s.value ?? null
  return {
    key: props.kind,
    icon: s.icon,
    tone: s.tone,
    caption: s.caption,
    loading: pending,
    value: pending ? null : (failed.value || value === null ? '–' : numberFormat.value.format(value)),
    bad: s.nonZero === 'bad' && (value ?? 0) > 0,
    warn: s.nonZero === 'warn' && (value ?? 0) > 0,
    onClick: () => router.push(s.to),
  }
})
</script>

<template>
  <KpiCard :tile="tile" class="h-full" />
</template>
