<script setup lang="ts">
import { computed, ref, onMounted, watch, onUnmounted } from 'vue'
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
  ctx.header.subTitle = t('admin.securityLog.title', {}, 'Security')
  ctx.header.icon = 'shield-alert'
  ctx.content.container = false
}), { immediate: true })

// Streamless security/ops store (logging/audit redesign Track A — the half with no
// aggregate stream): unknown-actor login attempts, probes, rate-limits, policy
// rejections, and operational actions. Cross-realm in the system DB; a tenant
// realm-admin sees their own realm's tenant-visible rows, the control-plane realm
// sees the full cross-realm log including platform-only operational rows.
interface SecurityLogEntry {
  Timestamp: string
  Realm: string | null
  Category: string
  EventType: string
  Level: string
  UserName: string | null
  Ip: string | null
  Status: string | null
  Reason: string | null
  Message: string
}

const entries = ref<SecurityLogEntry[]>([])
const loading = ref(true)
const selectedCategory = ref<string | null>(null)
let pollInterval: ReturnType<typeof setInterval> | null = null

// Category chips are derived from the loaded rows — only categories actually
// present show up, so the filter never offers an empty bucket.
const categories = computed(() =>
  [...new Set(entries.value.map((e) => e.Category))].sort())

const filteredEntries = computed(() =>
  selectedCategory.value
    ? entries.value.filter((e) => e.Category === selectedCategory.value)
    : entries.value)

async function loadEntries() {
  try {
    entries.value = await http.get<SecurityLogEntry[]>()
  } catch { /* ignore */ }
  finally { loading.value = false }
}

async function clearLog() {
  // Clearing is itself audited (audit.log_cleared) on the server.
  await http.delete()
  entries.value = []
}

onMounted(() => {
  loadEntries()
  pollInterval = setInterval(loadEntries, 5_000)
})

onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})

const gridBuilder = CoarGridBuilder.create<SecurityLogEntry>()
  .rowDataRef(filteredEntries)
  .searchHighlight()
  .rowClassRules({
    'security-log-warning': (p) => p.data?.Level === 'Warning',
    'security-log-error': (p) => p.data?.Level === 'Error',
  })
  .columns([
    (col) => col.date('Timestamp', { includeTime: true }).header('Time', 'admin.securityLog.time').width(180),
    (col) => col.field('Category').header('Category', 'admin.securityLog.category').width(140),
    (col) => col.field('EventType').header('Event', 'admin.securityLog.event').width(220),
    (col) => col.field('Message').header('Detail', 'admin.securityLog.detail').flex(1),
    (col) => col.field('UserName').header('Actor', 'admin.securityLog.actor').width(160),
    (col) => col.field('Ip').header('IP', 'admin.securityLog.ip').width(140),
    (col) => col.tag('Level', {
      variantMap: { Info: 'neutral', Warning: 'warning', Error: 'error' },
    }).header('Level', 'admin.securityLog.level').width(100),
    // Realm attribution — constant for a tenant admin (their own realm), varies
    // for the control-plane (system) realm which sees the full cross-realm log.
    (col) => col.field('Realm').header('Realm', 'admin.securityLog.realm').width(120),
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
        <div class="flex items-center gap-1 flex-wrap">
          <CoarButton size="s" :variant="selectedCategory === null ? 'primary' : 'ghost'"
            @click="selectedCategory = null">
            {{ t('admin.securityLog.allCategories', {}, 'All') }}
          </CoarButton>
          <CoarButton v-for="c in categories" :key="c" size="s"
            :variant="selectedCategory === c ? 'primary' : 'ghost'"
            @click="selectedCategory = c">
            {{ c }}
          </CoarButton>
        </div>
        <CoarButton size="s" variant="ghost" @click="loadEntries">
          {{ t('admin.securityLog.refresh', {}, 'Refresh') }}
        </CoarButton>
        <CoarButton size="s" variant="ghost" @click="clearLog">
          {{ t('admin.securityLog.clear', {}, 'Clear') }}
        </CoarButton>
      </template>
    </CoarDataGrid>
  </div>
</template>

<style>
.security-log-warning {
  color: var(--coar-text-semantic-warning, #92400e);
}
.security-log-error {
  color: var(--coar-text-semantic-error, #991b1b);
}
</style>
