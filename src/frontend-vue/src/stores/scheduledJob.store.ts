import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  ScheduledJobDto,
  ScheduledJobHistoryDto,
  ScheduledJobUpdateDto,
} from '@/models/ScheduledJob'

/**
 * Pinia store for the scheduled-jobs admin surface. Deliberately
 * non-`useEntityService`: jobs are compile-time-registered (no create or
 * delete), so the generic CRUD shape doesn't fit. The update + trigger
 * endpoints are explicit calls.
 *
 * Endpoint group: `/api/admin/jobs/*`. List + per-key history both
 * served from the same path.
 */
export const useScheduledJobStore = defineStore('scheduled-job', () => {
  const http = useHttpClient('/api/admin/jobs')

  const jobs = ref<ScheduledJobDto[]>([])
  const loaded = ref(false)

  async function loadAll(): Promise<ScheduledJobDto[]> {
    jobs.value = await http.get<ScheduledJobDto[]>()
    loaded.value = true
    return jobs.value
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
  }

  async function loadOne(key: string): Promise<ScheduledJobDto | null> {
    try {
      return await http.addPath(key).get<ScheduledJobDto>()
    } catch (e: any) {
      if (e?.status === 404) return null
      throw e
    }
  }

  async function loadHistory(key: string, take = 50): Promise<ScheduledJobHistoryDto[]> {
    return await http
      .addPath(key, 'history')
      .setQueryParameter('take', String(take))
      .get<ScheduledJobHistoryDto[]>()
  }

  async function update(key: string, dto: ScheduledJobUpdateDto): Promise<void> {
    await http.addPath(key).put(dto)
    // refresh local list so the grid reflects new cron / enabled state immediately
    await loadAll()
  }

  async function triggerNow(key: string): Promise<void> {
    await http.addPath(key, 'trigger').post({})
  }

  return {
    jobs,
    loaded,
    loadAll,
    initialize,
    loadOne,
    loadHistory,
    update,
    triggerNow,
  }
})
