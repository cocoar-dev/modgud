<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarTag } from '@cocoar/vue-ui'
import { useHttpClient } from '@/composables/useHttpClient'
import type { ClientSessionDto, SessionDto, SessionListDto } from '@/models/session'
import { useRelativeTime } from '../relativeTime'
import WidgetCard from './WidgetCard.vue'

const { t } = useI18n()
const router = useRouter()
const relativeTime = useRelativeTime()

type Row = (SessionDto & { Kind: 'Browser' }) | (ClientSessionDto & { Kind: 'Client' })

const rows = ref<Row[]>([])
const loading = ref(true)
const failed = ref(false)

onMounted(async () => {
  try {
    const res = await useHttpClient('/api/auth/sessions').get<SessionListDto>()
    rows.value = [
      ...(res.Sessions ?? []).map(s => ({ ...s, Kind: 'Browser' as const })),
      ...(res.ClientSessions ?? []).map(s => ({ ...s, Kind: 'Client' as const })),
    ].sort((a, b) => new Date(b.LastActiveAt).getTime() - new Date(a.LastActiveAt).getTime())
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
})

const top = computed(() => rows.value.slice(0, 3))
const extra = computed(() => Math.max(0, rows.value.length - 3))

function label(s: Row): string {
  if (s.Kind === 'Client') return s.ClientDisplayName || s.ClientId
  const browser = s.Browser || t('dashboard.sessions.unknownBrowser', {}, 'Browser')
  const os = s.OperatingSystem || s.DeviceType || t('dashboard.sessions.unknownDevice', {}, 'unknown device')
  return t('dashboard.sessions.deviceLabel', { browser, os }, '{browser} on {os}')
}
</script>

<template>
  <WidgetCard
    :title="t('dashboard.sessions.title', {}, 'Active sessions')"
    icon="monitor"
    :loading="loading"
    :failed="failed"
    :empty="rows.length === 0 ? t('dashboard.sessions.none', {}, 'No sessions.') : null"
    :cta="t('dashboard.sessions.cta', {}, 'Manage sessions →')"
    @open="router.push('/profile')"
  >
    <button
      v-for="s in top" :key="s.Id"
      type="button" class="widget-row"
      :class="{ 'widget-row--strong': s.Kind === 'Browser' && s.IsCurrent }"
      @click="router.push('/profile')"
    >
      <span class="widget-row__label"><span class="widget-row__title">{{ label(s) }}</span></span>
      <span class="widget-row__meta">
        <CoarTag v-if="s.Kind === 'Browser' && s.IsCurrent" variant="success" size="s">
          {{ t('dashboard.sessions.thisDevice', {}, 'This device') }}
        </CoarTag>
        <CoarTag v-else-if="s.Kind === 'Client'" variant="neutral" size="s">
          {{ t('dashboard.sessions.app', {}, 'App') }}
        </CoarTag>
        <CoarTag variant="neutral" size="s">{{ relativeTime(s.LastActiveAt) }}</CoarTag>
      </span>
    </button>
    <div v-if="extra > 0" class="widget-row__more">
      {{ t('dashboard.sessions.more', { n: extra }, '+{n} more') }}
    </div>
  </WidgetCard>
</template>
