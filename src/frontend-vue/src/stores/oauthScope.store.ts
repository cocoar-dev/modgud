import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useSignalR } from '@/composables/useSignalR'
import type {
  OAuthScopeDto,
  OAuthScopeListDto,
  CreateOAuthScopeDto,
  UpdateOAuthScopeDto,
} from '@/models/oauth'

interface OAuthDataEvent {
  Subject: string
  Action: 'Created' | 'Updated' | 'Deleted' | 'Custom' | 'FullSync'
  Payload: unknown[]
}

/**
 * OAuth scope store. Live-updated via the OAuthScopeActions SignalR stream
 * (Created/Updated → upsert, Deleted → remove), so scopes created out-of-band —
 * implicitly when an OAuth API is added, another admin, another tab — appear
 * without a manual reload. The list endpoint is paginated and returns
 * <c>{ Items, TotalCount }</c>; we unwrap <c>Items</c> for the grid.
 */
export const useOAuthScopeStore = defineStore('oauth-scope', () => {
  const http = useHttpClient('/api/admin/oauth/scopes')
  const signalr = useSignalR()

  const scopes = ref<OAuthScopeDto[]>([])
  const loaded = ref(false)
  const totalCount = ref(0)

  async function loadAll(): Promise<OAuthScopeDto[]> {
    const res = await http
      .setQueryParameter('page', '1')
      .setQueryParameter('pageSize', '500')
      .get<OAuthScopeListDto>()
    scopes.value = res.Items
    totalCount.value = res.TotalCount
    loaded.value = true
    return res.Items
  }

  async function initialize() {
    // Live updates (implicit scope-per-API, other admins/tabs). Mirrors
    // useEntityService: (re)subscribe + REST re-sync on every (re)connect,
    // de-duped by the stream key. The explicit initial loadAll below fills the
    // grid before SignalR is even connected.
    signalr.runOnEveryReconnect(() => {
      subscribeToSignalR()
      loadAll()
    }, 'OAuthScopeActions.Subscribe')

    if (!loaded.value) await loadAll()
  }

  function subscribeToSignalR() {
    signalr.stream<OAuthDataEvent>('OAuthScopeActions.Subscribe').subscribe({
      next: (ev) => {
        if (ev.Action === 'Created' || ev.Action === 'Updated') {
          let next = scopes.value
          for (const dto of ev.Payload as OAuthScopeDto[]) next = upsert(next, dto)
          scopes.value = next
        } else if (ev.Action === 'Deleted') {
          const ids = ev.Payload as string[]
          scopes.value = scopes.value.filter((s) => !ids.includes(s.Id))
        }
      },
      error: (err) => console.error('[oauth-scope] SignalR stream error:', err),
    })
  }

  async function loadOne(id: string): Promise<OAuthScopeDto | null> {
    try {
      const dto = await http.addPath(id).get<OAuthScopeDto>()
      scopes.value = upsert(scopes.value, dto)
      return dto
    } catch {
      return null
    }
  }

  async function create(dto: CreateOAuthScopeDto): Promise<OAuthScopeDto> {
    const created = await http.post<OAuthScopeDto>(dto)
    scopes.value = upsert(scopes.value, created)
    return created
  }

  async function update(id: string, dto: UpdateOAuthScopeDto): Promise<OAuthScopeDto> {
    const updated = await http.addPath(id).put<OAuthScopeDto>(dto)
    scopes.value = upsert(scopes.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await http.addPath(id).delete()
    scopes.value = scopes.value.filter((s) => s.Id !== id)
  }

  return {
    scopes,
    loaded,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    remove,
  }
})

function upsert(list: OAuthScopeDto[], item: OAuthScopeDto): OAuthScopeDto[] {
  const idx = list.findIndex((s) => s.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
