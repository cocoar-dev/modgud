<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'

// Embedded as the "Audit" tab of AdminLogsView — the header/sub-nav is owned by
// that wrapper, so this view is pure grid content.
const { t } = useI18n()
const http = useHttpClient('/api/admin/audit')

// Tenant GDPR-audit rows (logging/audit redesign Track A). Projected from the
// user + config event streams; PII inherits the source masking, so a GDPR-erased
// user surfaces here de-identified (no IP), never deleted.
interface AuditEntry {
  Timestamp: string
  Realm: string | null
  Category: string
  EventType: string
  User: string | null
  Ip: string | null
  Method: string | null
  Count: number | null
  Level: string
}

const entries = ref<AuditEntry[]>([])
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
    entries.value = await http.get<AuditEntry[]>()
  } catch { /* ignore */ }
  finally { loading.value = false }
}

onMounted(() => {
  loadEntries()
  pollInterval = setInterval(loadEntries, 5_000)
})

onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})

const gridBuilder = CoarGridBuilder.create<AuditEntry>()
  .rowDataRef(filteredEntries)
  .searchHighlight()
  .rowClassRules({
    'audit-warning': (p) => p.data?.Level === 'Warning',
    'audit-error': (p) => p.data?.Level === 'Error',
  })
  .columns([
    (col) => col.date('Timestamp', { includeTime: true }).header('Time', 'admin.auditLog.time').width(180),
    (col) => col.field('Category').header('Category', 'admin.auditLog.category').width(150),
    (col) => col.field('EventType').header('Event', 'admin.auditLog.event').flex(1),
    (col) => col.field('User').header('User', 'admin.auditLog.user').width(160),
    (col) => col.field('Method').header('Method', 'admin.auditLog.method').width(120),
    (col) => col.field('Ip').header('IP', 'admin.auditLog.ip').width(140),
    (col) => col.tag('Level', {
      variantMap: { Info: 'neutral', Warning: 'warning', Error: 'error' },
    }).header('Level', 'admin.auditLog.level').width(100),
    (col) => col.field('Realm').header('Realm', 'admin.auditLog.realm').width(120),
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
            {{ t('admin.auditLog.allCategories', {}, 'All') }}
          </CoarButton>
          <CoarButton v-for="c in categories" :key="c" size="s"
            :variant="selectedCategory === c ? 'primary' : 'ghost'"
            @click="selectedCategory = c">
            {{ c }}
          </CoarButton>
        </div>
        <CoarButton size="s" variant="ghost" @click="loadEntries">
          {{ t('admin.auditLog.refresh', {}, 'Refresh') }}
        </CoarButton>
      </template>
    </CoarDataGrid>
  </div>
</template>

<style>
.audit-warning {
  color: var(--coar-text-semantic-warning, #92400e);
}
.audit-error {
  color: var(--coar-text-semantic-error, #991b1b);
}
</style>
