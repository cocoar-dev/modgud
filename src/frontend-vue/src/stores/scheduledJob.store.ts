import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  ScheduledJobDto,
  ScheduledJobHistoryDto,
  ScheduledJobUpdateDto,
} from '@/models/ScheduledJob'

/**
 * Admin store for scheduled jobs. No SignalR — schedules change rarely and
 * the admin view polls / refreshes on demand. History endpoint returns the
 * last N runs per job (default 50, capped server-side at 500).
 *
 * Deliberately non-`useEntityService`: jobs are compile-time-registered
 * (no create or delete), so the generic CRUD shape doesn't fit.
 */
export const useScheduledJobStore = defineStore('scheduled-job', () => {
  const http = useHttpClient('/api/admin/jobs')

  const jobs = ref<ScheduledJobDto[]>([])
  const loading = ref(false)

  async function loadAll(): Promise<void> {
    loading.value = true
    try {
      jobs.value = await http.get<ScheduledJobDto[]>()
    } finally {
      loading.value = false
    }
  }

  async function getByKey(key: string): Promise<ScheduledJobDto | null> {
    try {
      return await http.addPath(key).get<ScheduledJobDto>()
    } catch {
      return null
    }
  }

  async function getHistory(key: string, take = 50): Promise<ScheduledJobHistoryDto[]> {
    return http
      .addPath(key, 'history')
      .setQueryParameter('take', String(take))
      .get<ScheduledJobHistoryDto[]>()
  }

  async function update(key: string, dto: ScheduledJobUpdateDto): Promise<void> {
    await http.addPath(key).put(dto)
    // Refresh the affected row locally so the UI reflects the new schedule
    // without a full reload.
    const fresh = await getByKey(key)
    if (fresh) {
      const i = jobs.value.findIndex((j) => j.Key === key)
      if (i >= 0) jobs.value[i] = fresh
    }
  }

  async function triggerNow(key: string): Promise<void> {
    await http.addPath(key, 'trigger').post({})
  }

  return { jobs, loading, loadAll, getByKey, getHistory, update, triggerNow }
})
