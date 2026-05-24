import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type { InboxRetentionSettings } from '@/models/inboxSettings'

/**
 * Admin store for the singleton InboxRetentionSettings doc. No SignalR —
 * read on mount, write on save. Backend uses one stable id; the
 * `/admin/inbox-settings` endpoint hides that detail.
 */
export const useInboxSettingsStore = defineStore('inboxSettings', () => {
  const http = useHttpClient('/api/admin/inbox-settings')
  const settings = ref<InboxRetentionSettings | null>(null)
  const loading = ref(false)

  async function load(): Promise<void> {
    loading.value = true
    try {
      settings.value = await http.get<InboxRetentionSettings>()
    } finally {
      loading.value = false
    }
  }

  async function save(updated: InboxRetentionSettings): Promise<void> {
    await http.put(updated)
    settings.value = updated
  }

  return { settings, loading, load, save }
})
