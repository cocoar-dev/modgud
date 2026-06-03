<script setup lang="ts">
import { computed, ref, onMounted, watch, onUnmounted } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton, CoarCheckbox } from '@cocoar/vue-ui'

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
  Realm: string | null
}

const entries = ref<AuthLogEntry[]>([])
const loading = ref(true)
const dcrOnly = ref(false)
let pollInterval: ReturnType<typeof setInterval> | null = null

// DCR audit lines all carry the "DCR " prefix on the Message column
// (see Modgud.Application/Dcr/DcrAuditEvents.cs for the canonical
// vocabulary). Filtering by prefix avoids needing a separate
// category-column migration on the AuthLogDocument.
const filteredEntries = computed(() =>
  dcrOnly.value
    ? entries.value.filter((e) => e.Message?.startsWith('DCR '))
    : entries.value)

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
  // Poll the auth-log frequently. The persistence service drains its
  // channel on a background task, so a "Login successful" entry fired
  // milliseconds before the grid mounts hasn't reached Marten yet — the
  // first manual page visit after auto-setup used to look as if the
  // event was missing entirely until the next 10s tick. 2s is a tolerable
  // network cost on what is already a low-traffic page, and a SignalR
  // push remains a worthwhile follow-up.
  pollInterval = setInterval(loadEntries, 2_000)
})

onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})

const gridBuilder = CoarGridBuilder.create<AuthLogEntry>()
  .rowDataRef(filteredEntries)
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
    // Realm attribution — constant for a tenant admin (their own realm), varies
    // for the control-plane (system) realm which sees the full cross-realm log.
    (col) => col.field('Realm').header('Realm', 'admin.authLog.realm').width(120),
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
        <CoarCheckbox v-model="dcrOnly"
          :label="t('admin.authLog.dcrOnly', {}, 'DCR events only')"
          :title="t('admin.authLog.dcrOnly.help', {}, 'Show only events from the Dynamic Client Registration endpoint (registration, rate-limit, GC). Matches any audit line beginning with the DCR prefix.')" />
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
