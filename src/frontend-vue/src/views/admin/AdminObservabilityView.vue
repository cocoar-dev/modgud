<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted, watch } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useSignalR } from '@/composables/useSignalR'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { CoarCard, CoarTag } from '@cocoar/vue-ui'

const { t, language } = useI18n()
const http = useHttpClient('/api/admin/observability')
const signalr = useSignalR()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.observability.title', {}, 'Observability')
  ctx.header.icon = 'activity'
  ctx.content.container = false
}), { immediate: true })

interface Snapshot {
  Realm: string
  WindowMinutes: number
  GeneratedAt: string
  Counts: Record<string, number>
  LoginByOutcome: Record<string, number>
  LoginSparkline: number[]
}

interface ActivityItem {
  Timestamp: string
  EventType: string
  Realm: string
  Tags: Record<string, string>
}

const snapshot = ref<Snapshot | null>(null)
const activity = ref<ActivityItem[]>([])
const lastUpdate = ref<Date | null>(null)
let driftRefreshHandle: ReturnType<typeof setInterval> | null = null

// Initial state + drift-correction: REST snapshot delivers the rolling-
// window aggregates and sparkline buckets pre-bucketed by the server.
// SignalR carries live events, but the sparkline buckets shift over time
// (every minute the window rolls forward) which the client can't fully
// reconstruct without a re-sync. 30s drift-refresh keeps the sparkline
// correctly aligned; counts are kept fresh by live events in between.
async function refreshSnapshot() {
  try {
    const [snap, act] = await Promise.all([
      http.addPath('snapshot').get<Snapshot>(),
      http.addPath('activity').setQueryParameter('limit', '50').get<ActivityItem[]>(),
    ])
    snapshot.value = snap
    activity.value = act
    lastUpdate.value = new Date()
  } catch { /* swallow — keep previous values rather than blink */ }
}

function applyLiveEvent(ev: ActivityItem) {
  // Prepend to feed (cap at 50).
  activity.value = [ev, ...activity.value].slice(0, 50)
  lastUpdate.value = new Date()

  // Incrementally update the snapshot in place — keeps KPI cards live
  // without waiting for the next drift refresh.
  const s = snapshot.value
  if (!s) return

  s.Counts[ev.EventType] = (s.Counts[ev.EventType] ?? 0) + 1
  if (ev.EventType === 'login') {
    const outcome = ev.Tags.outcome ?? 'unknown'
    s.LoginByOutcome[outcome] = (s.LoginByOutcome[outcome] ?? 0) + 1
    // Latest bucket of the sparkline = current minute. Increment in place.
    const last = s.LoginSparkline.length - 1
    if (last >= 0) s.LoginSparkline[last] = (s.LoginSparkline[last] ?? 0) + 1
  }
}

onMounted(() => {
  refreshSnapshot()
  driftRefreshHandle = setInterval(refreshSnapshot, 30_000)

  signalr.runOnEveryReconnect(() => {
    signalr.stream<ActivityItem>('Observability.Subscribe').subscribe({
      next: applyLiveEvent,
      error: (err) => console.error('[observability] stream error', err),
    })
  }, 'AdminObservabilityView.Observability.Subscribe')
})

onUnmounted(() => {
  if (driftRefreshHandle) clearInterval(driftRefreshHandle)
})

// KPI cards — pulled from the LoginByOutcome breakdown.
const kpis = computed(() => {
  const s = snapshot.value
  if (!s) return []
  return [
    {
      key: 'success',
      label: t('admin.observability.loginSuccess', {}, 'Login successes'),
      value: s.LoginByOutcome.success ?? 0,
      tone: 'positive',
    },
    {
      key: 'failure',
      label: t('admin.observability.loginFailure', {}, 'Login failures'),
      value: s.LoginByOutcome.failure ?? 0,
      tone: 'critical',
    },
    {
      key: '2fa',
      label: t('admin.observability.twoFactorBlocks', {}, '2FA-enforcement blocks'),
      value: s.Counts['two_factor.blocked'] ?? 0,
      tone: 'warning',
    },
    {
      key: 'refresh',
      label: t('admin.observability.refreshRejected', {}, 'Refresh-token rejections'),
      value: s.Counts['token.refresh.rejected'] ?? 0,
      tone: 'warning',
    },
    {
      key: 'tokens',
      label: t('admin.observability.tokensMinted', {}, 'Tokens minted'),
      value: s.Counts['token.minted'] ?? 0,
      tone: 'neutral',
    },
    {
      key: 'dcr',
      label: t('admin.observability.dcrRegistrations', {}, 'DCR registrations'),
      value: s.Counts['dcr.registration'] ?? 0,
      tone: 'neutral',
    },
  ]
})

// Inline SVG sparkline — one polyline through `windowMinutes` data points.
const sparklinePath = computed(() => {
  const data = snapshot.value?.LoginSparkline ?? []
  if (data.length === 0) return ''
  const max = Math.max(1, ...data)
  const w = 600
  const h = 80
  const stepX = w / Math.max(1, data.length - 1)
  return data
    .map((v, i) => {
      const x = i * stepX
      const y = h - (v / max) * (h - 4) - 2
      return `${i === 0 ? 'M' : 'L'} ${x.toFixed(1)} ${y.toFixed(1)}`
    })
    .join(' ')
})

const sparklineMax = computed(() => {
  const data = snapshot.value?.LoginSparkline ?? []
  return Math.max(0, ...data)
})

function eventTone(type: string, tags: Record<string, string>): 'neutral' | 'positive' | 'warning' | 'critical' {
  if (type === 'login') {
    if (tags.outcome === 'success') return 'positive'
    if (tags.outcome === 'locked') return 'critical'
    if (tags.outcome === '2fa_required') return 'neutral'
    return 'warning'
  }
  if (type === 'two_factor.blocked') return 'warning'
  if (type === 'token.refresh.rejected') return 'critical'
  if (type === 'dcr.rate_limit.hit') return 'warning'
  return 'neutral'
}

function describe(item: ActivityItem): string {
  const tags = Object.entries(item.Tags)
    .map(([k, v]) => `${k}=${v}`)
    .join(' ')
  return tags ? `${item.EventType}  ${tags}` : item.EventType
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleTimeString(language.value, { hour12: false })
}

const toneClasses: Record<string, string> = {
  positive: 'kpi-positive',
  warning: 'kpi-warning',
  critical: 'kpi-critical',
  neutral: 'kpi-neutral',
}
</script>

<template>
  <div class="obs-page">
    <!-- KPI strip -->
    <div class="kpi-grid">
      <CoarCard
        v-for="kpi in kpis"
        :key="kpi.key"
        :class="['kpi-card', toneClasses[kpi.tone]]"
      >
        <div class="kpi-value">{{ kpi.value }}</div>
        <div class="kpi-label">{{ kpi.label }}</div>
      </CoarCard>
    </div>

    <!-- Sparkline -->
    <CoarCard class="spark-card">
      <div class="spark-header">
        <div>
          <div class="spark-title">{{ t('admin.observability.loginRate', {}, 'Logins per minute') }}</div>
          <div class="spark-meta">
            {{ t('admin.observability.windowLabel', {}, 'Rolling window') }}
            ·
            {{ snapshot?.WindowMinutes ?? 15 }} {{ t('admin.observability.minutes', {}, 'min') }}
            ·
            {{ t('admin.observability.peak', {}, 'Peak') }} {{ sparklineMax }}
          </div>
        </div>
        <div class="spark-realm">
          <CoarTag variant="neutral">{{ snapshot?.Realm ?? '—' }}</CoarTag>
        </div>
      </div>
      <svg class="spark-svg" viewBox="0 0 600 80" preserveAspectRatio="none">
        <path :d="sparklinePath" fill="none" stroke="currentColor" stroke-width="1.5" />
      </svg>
    </CoarCard>

    <!-- Activity feed -->
    <CoarCard class="feed-card">
      <div class="feed-header">
        <div class="feed-title">{{ t('admin.observability.activityFeed', {}, 'Recent activity') }}</div>
        <div class="feed-meta" v-if="lastUpdate">
          {{ t('admin.observability.updated', {}, 'Updated') }}
          {{ lastUpdate.toLocaleTimeString(language, { hour12: false }) }}
        </div>
      </div>
      <ul class="feed-list" v-if="activity.length > 0">
        <li v-for="(item, i) in activity" :key="i" class="feed-item">
          <span class="feed-time">{{ formatTime(item.Timestamp) }}</span>
          <CoarTag :variant="eventTone(item.EventType, item.Tags) === 'critical' ? 'error'
                              : eventTone(item.EventType, item.Tags) === 'warning' ? 'warning'
                              : eventTone(item.EventType, item.Tags) === 'positive' ? 'success'
                              : 'neutral'">
            {{ item.EventType }}
          </CoarTag>
          <span class="feed-detail">{{ describe(item) }}</span>
        </li>
      </ul>
      <div v-else class="feed-empty">
        {{ t('admin.observability.empty', {}, 'No events in the rolling window.') }}
      </div>
    </CoarCard>
  </div>
</template>

<style scoped>
.obs-page {
  display: flex;
  flex-direction: column;
  gap: 1rem;
  padding: 1rem;
  min-height: 0;
  flex: 1;
}

.kpi-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.75rem;
}

.kpi-card {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  padding: 0.75rem 1rem;
}

.kpi-value {
  font-size: 1.75rem;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  line-height: 1.1;
}

.kpi-label {
  font-size: 0.8rem;
  color: var(--coar-text-neutral-secondary);
}

.kpi-positive .kpi-value { color: var(--coar-text-semantic-success, #16a34a); }
.kpi-warning  .kpi-value { color: var(--coar-text-semantic-warning, #d97706); }
.kpi-critical .kpi-value { color: var(--coar-text-semantic-error,   #dc2626); }
.kpi-neutral  .kpi-value { color: var(--coar-text-neutral-primary); }

.spark-card { padding: 0.75rem 1rem; }

.spark-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 0.5rem;
}

.spark-title {
  font-size: 0.9rem;
  font-weight: 600;
}

.spark-meta {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary);
  margin-top: 0.125rem;
}

.spark-svg {
  width: 100%;
  height: 80px;
  color: var(--coar-text-accent-primary, #4f46e5);
}

.feed-card {
  padding: 0.75rem 1rem;
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.feed-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.5rem;
}

.feed-title {
  font-size: 0.9rem;
  font-weight: 600;
}

.feed-meta {
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary);
}

.feed-list {
  list-style: none;
  margin: 0;
  padding: 0;
  overflow-y: auto;
  flex: 1;
}

.feed-item {
  display: grid;
  grid-template-columns: 80px auto 1fr;
  gap: 0.75rem;
  align-items: center;
  padding: 0.4rem 0;
  border-bottom: 1px solid var(--coar-border-neutral-secondary);
  font-size: 0.85rem;
}

.feed-item:last-child {
  border-bottom: 0;
}

.feed-time {
  color: var(--coar-text-neutral-secondary);
  font-variant-numeric: tabular-nums;
}

.feed-detail {
  color: var(--coar-text-neutral-primary);
  font-family: var(--coar-font-mono, ui-monospace, monospace);
  font-size: 0.78rem;
  word-break: break-all;
}

.feed-empty {
  padding: 1.5rem;
  text-align: center;
  color: var(--coar-text-neutral-secondary);
}
</style>
