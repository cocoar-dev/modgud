<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useScheduledJobStore } from '@/stores/scheduledJob.store'
import { useUI } from '@/composables/useUI'
import type { ScheduledJobDto } from '@/models/ScheduledJob'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useScheduledJobStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.scheduledJobs.title', {}, 'Scheduled Jobs')
  ctx.header.icon = 'clock'
  ctx.content.container = false
}), { immediate: true })

onMounted(() => store.initialize())

const rows = computed(() => store.jobs)

const builder = CoarGridBuilder.create<ScheduledJobDto>()
  .persistColumnState('admin-scheduled-jobs')
  .option('getRowId', (p: any) => p.data.Key)
  .rowDataRef(rows)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event) => {
    if (event.data) navigateToModal(event.data.Key)
  })
  .columns([
    (col) => col.icon('LastRunStatus', { size: 's' })
      .option('valueGetter', (p: any) => {
        const last = p.data?.LastRun
        if (!last) return 'minus'
        return last.Success ? 'check-circle' : 'x-circle'
      })
      .header('').width(48).resizable(false),
    (col) => col.field('Name').header('Name', 'admin.scheduledJobs.name').flex(2),
    (col) => col.field('EffectiveCron').header('Cron', 'admin.scheduledJobs.cron').width(160)
      .option('valueGetter', (p: any) => {
        if (!p.data) return ''
        const base = p.data.EffectiveCron as string
        return p.data.HasOverride
          ? `${base} · ${t('admin.scheduledJobs.overrideTag', {}, 'override')}`
          : base
      }),
    (col) => col.date('NextFireAt', { includeTime: true }).header('Next', 'admin.scheduledJobs.nextFire').width(170),
    (col) => col.date('LastRunStartedAt', { includeTime: true })
      .option('valueGetter', (p: any) => p.data?.LastRun?.StartedAt ?? null)
      .header('Last Run', 'admin.scheduledJobs.lastRun').width(170),
    (col) => col.field('LastRunSummary')
      .option('valueGetter', (p: any) => p.data?.LastRun?.ResultSummary ?? p.data?.LastRun?.ErrorMessage ?? '')
      .header('Result', 'admin.scheduledJobs.lastResult').flex(2),
    (col) => col.field('Enabled').header('Enabled', 'admin.scheduledJobs.enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled
        ? t('common.yes', {}, 'Yes')
        : t('common.no', {}, 'No')),
  ])
</script>

<template>
  <CoarDataGrid :builder="builder" show-search :empty-message="t('admin.scheduledJobs.empty', {}, 'No jobs registered')" />
</template>
