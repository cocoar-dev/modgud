<script setup lang="ts">
import { ref, onMounted, watch, onUnmounted } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'

const { t, language } = useI18n()
const http = useHttpClient('/api/admin/auth-log')

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.authLog.title', {}, 'Auth Log')
  ctx.header.icon = 'scroll-text'
  ctx.content.container = false
}), { immediate: true })

interface AuthLogEntry {
  Timestamp: string
  Level: string
  Message: string
  UserName: string | null
  Ip: string | null
}

const entries = ref<AuthLogEntry[]>([])
const loading = ref(true)
let pollInterval: ReturnType<typeof setInterval> | null = null

async function loadEntries() {
  try {
    entries.value = await http.get<AuthLogEntry[]>()
  } catch { /* ignore */ }
  finally { loading.value = false }
}

async function clearLog() {
  await http.delete()
  entries.value = []
}

onMounted(() => {
  loadEntries()
  pollInterval = setInterval(loadEntries, 10_000)
})

onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})

const gridBuilder = CoarGridBuilder.create<AuthLogEntry>()
  .rowDataRef(entries)
  .searchHighlight()
  .rowClassRules({
    'auth-log-warning': (p) => p.data?.Level === 'Warning',
    'auth-log-error': (p) => p.data?.Level === 'Error',
  })
  .columns([
    (col) => col.date('Timestamp', { includeTime: true }).header('Time', 'admin.authLog.time').width(180),
    (col) => col.tag('Level', {
      variantMap: { Info: 'neutral', Warning: 'warning', Error: 'error' },
    }).header('Level', 'admin.authLog.level').width(100),
    (col) => col.field('Message').header('Event', 'admin.authLog.event').flex(1),
    (col) => col.field('UserName').header('User', 'admin.authLog.user').width(120),
    (col) => col.field('Ip').header('IP', 'admin.authLog.ip').width(140),
  ])
</script>

<template>
  <div class="flex flex-col flex-1 min-h-0 p-4">
    <CoarDataGrid
      :builder="gridBuilder"
      show-search
      class="h-full"
      bordered
      elevated
    >
      <template #toolbar-right>
        <CoarButton size="s" variant="ghost" @click="loadEntries">
          {{ t('admin.authLog.refresh', {}, 'Refresh') }}
        </CoarButton>
        <CoarButton size="s" variant="ghost" @click="clearLog">
          {{ t('admin.authLog.clear', {}, 'Clear') }}
        </CoarButton>
      </template>
    </CoarDataGrid>
  </div>
</template>

<style>
.auth-log-warning {
  color: var(--coar-text-semantic-warning, #92400e);
}
.auth-log-error {
  color: var(--coar-text-semantic-error, #991b1b);
}
</style>
