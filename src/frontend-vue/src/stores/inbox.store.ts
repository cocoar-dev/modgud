import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useSignalR } from '@/composables/useSignalR'
import type { InboxCountDto, InboxItemDto } from '@/models/InboxItem'

interface InboxHubEvent {
  Action: 'Created' | 'Updated' | 'Deleted'
  Subject: string
  Payload: unknown[]
}

/**
 * Per-user inbox store. Loads the open items on first access, then live-pushes
 * via SignalR (InboxHub on the backend filters per-recipient, so this client
 * stream is already user-scoped — no extra check needed here).
 *
 * Items in `items` are open (DismissedAt === null). The bell badge reads
 * `unreadCount`; the panel renders `items` ordered newest-first.
 */
export const useInboxStore = defineStore('inbox', () => {
  const http = useHttpClient('/api/inbox')
  const signalr = useSignalR()

  const items = ref<InboxItemDto[]>([])
  const loaded = ref(false)

  let signalrSubscribed = false

  const unreadCount = computed(() => items.value.filter((i) => i.ReadAt == null).length)
  const totalCount = computed(() => items.value.length)

  async function loadAll(): Promise<void> {
    // includeRead=true so the panel shows the recent context, not just the
    // unread set. The badge derives Unread from the in-memory list above.
    items.value = await http
      .setQueryParameter('includeRead', 'true')
      .setQueryParameter('includeDismissed', 'false')
      .setQueryParameter('take', '100')
      .get<InboxItemDto[]>()
    loaded.value = true
  }

  async function loadCount(): Promise<InboxCountDto> {
    return await http.addPath('count').get<InboxCountDto>()
  }

  async function markRead(id: string): Promise<void> {
    await http.addPath(id, 'read').post({})
    const item = items.value.find((i) => i.Id === id)
    if (item) item.ReadAt = new Date().toISOString()
  }

  async function markAllRead(): Promise<void> {
    await http.addPath('read-all').post({})
    const now = new Date().toISOString()
    for (const i of items.value) if (i.ReadAt == null) i.ReadAt = now
  }

  async function dismiss(id: string): Promise<void> {
    await http.addPath(id, 'dismiss').post({})
    items.value = items.value.filter((i) => i.Id !== id)
  }

  async function dismissAll(): Promise<void> {
    await http.addPath('dismiss-all').post({})
    items.value = []
  }

  function ensureSubscription() {
    if (signalrSubscribed) return
    signalrSubscribed = true
    signalr.runOnEveryReconnect(() => {
      signalr.stream<InboxHubEvent>('InboxActions.Subscribe').subscribe({
        next: (ev) => {
          for (const p of ev.Payload) {
            if (typeof p !== 'object' || p === null) continue
            const dto = p as InboxItemDto
            if (!('Id' in dto)) continue

            const idx = items.value.findIndex((i) => i.Id === dto.Id)
            if (dto.DismissedAt != null) {
              if (idx >= 0) items.value.splice(idx, 1)
              continue
            }
            if (idx >= 0) {
              items.value[idx] = dto
            } else {
              // Insert newest-first; createdAt-desc maintained.
              items.value = [dto, ...items.value]
            }
          }
        },
        error: (err) => console.error('[inbox.store] InboxActions stream error:', err),
      })
    }, 'inbox.store.InboxActions.Subscribe')
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
    ensureSubscription()
  }

  return {
    items,
    loaded,
    unreadCount,
    totalCount,
    loadAll,
    loadCount,
    markRead,
    markAllRead,
    dismiss,
    dismissAll,
    initialize,
  }
})
