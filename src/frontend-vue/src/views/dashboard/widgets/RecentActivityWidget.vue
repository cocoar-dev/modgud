<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from '@cocoar/vue-localization'
import { CoarTag } from '@cocoar/vue-ui'
import { useHttpClient } from '@/composables/useHttpClient'
import type { WidgetOptions } from '../layout'
import { useRelativeTime } from '../relativeTime'
import WidgetCard from './WidgetCard.vue'

const props = defineProps<{ options: WidgetOptions }>()

const { t } = useI18n()
const router = useRouter()
const relativeTime = useRelativeTime()

// The fields this widget reads from RealmSecurityLogDto (AuthLogEndpoints.cs).
interface SecurityLogRow {
  Id: string
  Timestamp: string
  Severity: 'Info' | 'Warning' | 'Error'
  Actor: string
  Message: string
}

// Loaded once at the largest choice; the setting only trims what is shown.
const MAX_ROWS = 15
const all = ref<SecurityLogRow[]>([])
const rows = computed(() => all.value.slice(0, Number(props.options.rows?.[0]) || 8))
const loading = ref(true)
const failed = ref(false)

onMounted(async () => {
  try {
    all.value = await useHttpClient('/api/admin/auth-log').setQueryParameter('limit', String(MAX_ROWS)).get<SecurityLogRow[]>()
  } catch {
    failed.value = true
  } finally {
    loading.value = false
  }
})

function variant(severity: SecurityLogRow['Severity']): 'neutral' | 'warning' | 'error' {
  return severity === 'Error' ? 'error' : severity === 'Warning' ? 'warning' : 'neutral'
}
</script>

<template>
  <WidgetCard
    :title="t('dashboard.systemActivity.title', {}, 'Recent security events')"
    icon="scroll-text"
    :loading="loading"
    :failed="failed"
    :empty="rows.length === 0 ? t('dashboard.systemActivity.none', {}, 'No events yet.') : null"
    :cta="t('dashboard.systemActivity.cta', {}, 'View all →')"
    @open="router.push('/admin/logs')"
  >
    <button
      v-for="row in rows" :key="row.Id"
      type="button" class="widget-row" :class="{ 'widget-row--strong': row.Severity === 'Error' }"
      @click="router.push('/admin/logs')"
    >
      <span class="widget-row__label">
        <span class="widget-row__title">{{ row.Message }}</span>
        <span class="widget-row__sub">{{ row.Actor }}</span>
      </span>
      <span class="widget-row__meta">
        <CoarTag :variant="variant(row.Severity)" size="s">{{ relativeTime(row.Timestamp) }}</CoarTag>
      </span>
    </button>
  </WidgetCard>
</template>
