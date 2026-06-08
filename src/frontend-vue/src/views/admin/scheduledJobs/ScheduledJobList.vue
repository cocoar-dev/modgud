<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { useScheduledJobStore } from '@/stores/scheduledJob.store'
import GridEmptyState from '@/components/GridEmptyState.vue'
import type { ScheduledJobDto } from '@/models/ScheduledJob'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = [
    { label: t('nav.administration', {}, 'Administration'), to: '/admin' },
    { label: t('admin.scheduledJobs.title', {}, 'Scheduled Jobs') },
  ]
  ctx.header.icon = 'clock'
  ctx.content.container = false
}), { immediate: true })

const store = useScheduledJobStore()
const { jobs, loading } = storeToRefs(store)

// Jobs are compile-time registered, so an empty grid means the registry has
// not loaded rather than "nothing to create" — no CTA, just an explanation.
const showEmpty = computed(() => !loading.value && jobs.value.length === 0)

onMounted(() => store.loadAll())

function lastRunVariant(job: ScheduledJobDto): 'success' | 'error' | 'neutral' {
  if (!job.LastRun) return 'neutral'
  return job.LastRun.Success ? 'success' : 'error'
}

const builder = applyListGridDefaults(CoarGridBuilder.create<ScheduledJobDto>(), { openable: true })
  .option('getRowId', (p: any) => p.data.Key)
  .rowDataRef(jobs)
  .searchHighlight()
  .onCellDoubleClicked((event) => {
    if (event.data) navigateToModal(event.data.Key)
  })
  .columns([
    (col) => col.field('Name').header('Job', 'admin.scheduledJobs.name').flex(1),
    (col) => col.field('Description').header('Description', 'admin.scheduledJobs.description').flex(2),
    (col) => col.field('EffectiveCron').header('Cron', 'admin.scheduledJobs.cron').width(140),
    (col) => col.tag('Enabled', {
      variantMap: { true: 'success', false: 'neutral' },
      i18nPrefix: 'admin.scheduledJobs.enabledTag.',
    })
      .header('Enabled', 'admin.scheduledJobs.enabled').width(110)
      .option('valueGetter', (p: any) => p.data?.Enabled ? 'true' : 'false'),
    (col) => col.date('NextFireAt', { includeTime: true })
      .header('Next run', 'admin.scheduledJobs.nextRun').width(170),
    (col) => col.date('LastRunStartedAt', { includeTime: true })
      .header('Last run', 'admin.scheduledJobs.lastRun').width(170)
      .option('valueGetter', (p: any) => p.data?.LastRun?.StartedAt ?? null),
    (col) => col.tag('LastRunStatus', {
      variantMap: { success: 'success', error: 'error', neutral: 'neutral' },
      i18nPrefix: 'admin.scheduledJobs.statusTag.',
    })
      .header('Status', 'admin.scheduledJobs.lastStatus').width(110)
      .option('valueGetter', (p: any) => lastRunVariant(p.data)),
  ])
</script>

<template>
  <div class="flex flex-col flex-1 min-h-0 p-4">
    <CoarDataGrid
      v-show="!showEmpty"
      :builder="builder"
      :search-placeholder="searchPlaceholder"
      show-search
      class="h-full"
      bordered
      elevated
    >
      <template #toolbar-right>
        <CoarButton size="s" variant="ghost" icon-start="rotate-ccw" @click="store.loadAll()">
          {{ t('common.refresh', {}, 'Refresh') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="clock"
      :title="t('admin.scheduledJobs.title', {}, 'Scheduled Jobs')"
      :description="t('admin.scheduledJobs.emptyHint', {}, 'Scheduled jobs are background tasks the server runs on a cron schedule (cleanup, sweeps, notifications). They are registered in code and appear here once loaded.')"
    />
  </div>
</template>
