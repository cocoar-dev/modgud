<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'
import { useGridLocale } from '@/composables/useGridLocale'
import GridEmptyState from '@/components/GridEmptyState.vue'

interface PlatformAuditEntry {
  Id: string
  Timestamp: string
  Category: string
  EventType: string
  Severity: string
  OutcomeCode: string
  ReasonCode: string | null
  OperationCode: string | null
  TargetRealmSlug: string | null
  CorrelationId: string | null
  Count: number | null
  RelatedCount: number | null
  Message: string
}

const { t } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
const http = useHttpClient('/api/admin/platform-audit')
const entries = ref<PlatformAuditEntry[]>([])
const loading = ref(true)
let pollInterval: ReturnType<typeof setInterval> | null = null

async function loadEntries() {
  try {
    entries.value = await http.get<PlatformAuditEntry[]>()
  } catch { /* endpoint and permission errors stay non-disruptive in the tab */ }
  finally { loading.value = false }
}

onMounted(() => {
  loadEntries()
  pollInterval = setInterval(loadEntries, 10_000)
})
onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})

const showEmpty = computed(() => !loading.value && entries.value.length === 0)
const gridBuilder = applyListGridDefaults(CoarGridBuilder.create<PlatformAuditEntry>(), { openable: false })
  .rowDataRef(entries)
  .searchHighlight()
  .rowClassRules({
    'platform-log-warning': (p) => p.data?.Severity === 'Warning',
    'platform-log-error': (p) => p.data?.Severity === 'Error',
  })
  .columns([
    (col) => col.date('Timestamp', { includeTime: true }).header('Time', 'admin.securityLog.time').width(180),
    (col) => col.field('EventType').header('Event', 'admin.securityLog.event').width(220),
    (col) => col.field('Message').header('Detail', 'admin.securityLog.detail').flex(1),
    (col) => col.field('OperationCode').header('Operation', 'admin.platformLog.operation').width(180),
    (col) => col.field('TargetRealmSlug').header('Target realm', 'admin.platformLog.targetRealm').width(140),
    (col) => col.tag('Severity', {
      variantMap: { Info: 'neutral', Warning: 'warning', Error: 'error' },
    }).header('Level', 'admin.securityLog.level').width(100),
  ])
</script>

<template>
  <div class="flex flex-col flex-1 min-h-0 p-4">
    <CoarDataGrid
      v-show="!showEmpty"
      :builder="gridBuilder"
      :search-placeholder="searchPlaceholder"
      show-search
      class="h-full"
      bordered
      elevated
    >
      <template #toolbar-right>
        <CoarButton size="s" variant="ghost" @click="loadEntries">
          {{ t('admin.securityLog.refresh', {}, 'Refresh') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="server-cog"
      :title="t('admin.platformLog.emptyTitle', {}, 'No platform events yet')"
      :description="t('admin.platformLog.emptyHint', {}, 'PII-free deployment-wide operations appear here.')"
    />
  </div>
</template>

<style>
.platform-log-warning {
  color: var(--coar-text-semantic-warning, #92400e);
}
.platform-log-error {
  color: var(--coar-text-semantic-error, #991b1b);
}
</style>
