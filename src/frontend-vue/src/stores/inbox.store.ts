import { defineStore } from 'pinia'
import { computed } from 'vue'
import { useEntityService } from '@/composables/useEntityService'
import type { InboxItemDto } from '@/models/InboxItem'

/**
 * Inbox store — notifications, reminders and other user-targeted messages.
 *
 * The backend emits per-recipient SignalR pushes (InboxHub filters server-side),
 * so everything that arrives is already addressed to the current user. The
 * store does not deduplicate or re-fetch on user change — login/logout fully
 * re-creates the Pinia instance.
 *
 * Dismissed items stay in the underlying entity map (the SignalR Updated
 * event lands with DismissedAt set), but `items` filters them out so the
 * bell/panel never see them.
 */
export const useInboxStore = defineStore('inbox', () => {
  const service = useEntityService<InboxItemDto>({
    apiPath: '/api/inbox',
    entityName: 'Inbox',
    enableSignalR: true,
    // Initialization happens lazily after auth — see InboxBell's onMounted.
    loadOnInit: false,
  })

  /** Sorted newest-first, dismissed filtered — what the panel renders. */
  const items = computed(() =>
    service.entities.value
      .filter((i) => !i.DismissedAt)
      .sort((a, b) => b.CreatedAt.localeCompare(a.CreatedAt)),
  )

  const unreadCount = computed(() =>
    items.value.filter((i) => !i.ReadAt).length,
  )

  /** Group items by Kind for filter tabs. */
  const itemsByKind = computed(() => {
    const out: Record<string, InboxItemDto[]> = {}
    for (const i of items.value) {
      const key = i.Kind
      if (!out[key]) out[key] = []
      out[key].push(i)
    }
    return out
  })

  async function markRead(id: string): Promise<void> {
    // Optimistic update — the SignalR Updated event will confirm.
    const existing = service.getFromStore(id)
    if (existing && !existing.ReadAt) {
      service.setStoreEntities([{ ...existing, ReadAt: new Date().toISOString() }])
    }
    try {
      await service.httpClient.addPath(id, 'read').post({})
    } catch (err) {
      if (existing) service.setStoreEntities([existing])
      throw err
    }
  }

  async function markAllRead(): Promise<void> {
    const now = new Date().toISOString()
    const snapshots: InboxItemDto[] = []
    for (const item of items.value) {
      if (!item.ReadAt) {
        snapshots.push(item)
        service.setStoreEntities([{ ...item, ReadAt: now }])
      }
    }
    try {
      await service.httpClient.addPath('read-all').post({})
    } catch (err) {
      if (snapshots.length > 0) service.setStoreEntities(snapshots)
      throw err
    }
  }

  async function dismiss(id: string): Promise<void> {
    const existing = service.getFromStore(id)
    // Optimistic remove — items computed filters by DismissedAt, but the
    // backend doesn't emit Deletes for dismiss (it emits Updated), so we
    // also nudge it out of the entity store directly for snappy UX.
    if (existing) service.deleteStoreEntities([id])
    try {
      await service.httpClient.addPath(id, 'dismiss').post({})
    } catch (err) {
      if (existing) service.setStoreEntities([existing])
      throw err
    }
  }

  async function dismissAll(): Promise<void> {
    const ids = items.value.map((i) => i.Id)
    if (ids.length === 0) return
    service.deleteStoreEntities(ids)
    try {
      await service.httpClient.addPath('dismiss-all').post({})
    } catch (err) {
      // Best-effort restore. Refetch to recover from inconsistent state.
      await service.loadAll()
      throw err
    }
  }

  async function snooze(id: string, until: Date | null): Promise<void> {
    await service.httpClient
      .addPath(id, 'snooze')
      .post({ Until: until?.toISOString() ?? null })
  }

  return {
    ...service,
    items,
    unreadCount,
    itemsByKind,
    markRead,
    markAllRead,
    dismiss,
    dismissAll,
    snooze,
  }
})
