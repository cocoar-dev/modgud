<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'
import { useGridLocale } from '@/composables/useGridLocale'
import GridEmptyState from '@/components/GridEmptyState.vue'

// Embedded as the "Security" tab of AdminLogsView — the header/sub-nav is owned
// by that wrapper, so this view is pure grid content.
const { t } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
const http = useHttpClient('/api/admin/auth-log')

// Realm-owned structured security events. This endpoint reads the current
// realm's physical database only, including when the current realm is the
// Control Plane.
interface SecurityLogEntry {
  Id: string
  Timestamp: string
  Category: string
  EventType: string
  Severity: string
  ActorKind: string
  Actor: string
  Target: string | null
  IpAddress: string | null
  UserAgent: string | null
  OAuthClientId: string | null
  AuthenticationMethod: string | null
  CorrelationId: string | null
  OutcomeCode: string
  ReasonCode: string | null
  TargetRealmSlug: string | null
  FirstObservedAt: string | null
  LastObservedAt: string | null
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

onMounted(() => {
  loadEntries()
  pollInterval = setInterval(loadEntries, 5_000)
})

onUnmounted(() => {
  if (pollInterval) clearInterval(pollInterval)
})

// Read-only log → no row-open affordance (openable: false). Onboarding
// empty-state keys off the raw row count so a category-filtered-to-empty grid
// keeps its chips + localized "no rows" overlay instead.
const showEmpty = computed(() => !loading.value && entries.value.length === 0)

const gridBuilder = applyListGridDefaults(CoarGridBuilder.create<SecurityLogEntry>(), { openable: false })
  .rowDataRef(filteredEntries)
  .searchHighlight()
  .rowClassRules({
    'security-log-warning': (p) => p.data?.Severity === 'Warning',
    'security-log-error': (p) => p.data?.Severity === 'Error',
  })
  .columns([
    (col) => col.date('Timestamp', { includeTime: true }).header('Time', 'admin.securityLog.time').width(180),
    (col) => col.field('Category').header('Category', 'admin.securityLog.category').width(140),
    (col) => col.field('EventType').header('Event', 'admin.securityLog.event').width(220),
    (col) => col.field('Message').header('Detail', 'admin.securityLog.detail').flex(1),
    (col) => col.field('Actor').header('Actor', 'admin.securityLog.actor').width(170),
    (col) => col.field('Target').header('Target', 'admin.securityLog.target').width(170),
    (col) => col.field('TargetRealmSlug').header('Target realm', 'admin.platformLog.targetRealm').width(140),
    (col) => col.field('IpAddress').header('IP', 'admin.securityLog.ip').width(140),
    (col) => col.field('AuthenticationMethod').header('Method', 'admin.securityLog.method').width(110),
    (col) => col.field('OAuthClientId').header('Client', 'admin.securityLog.client').width(160),
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
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="shield"
      :title="t('admin.securityLog.emptyTitle', {}, 'No security events yet')"
      :description="t('admin.securityLog.emptyHint', {}, 'Login attempts, lockouts, rate-limits and security-relevant actions appear here as they happen.')"
    />
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
