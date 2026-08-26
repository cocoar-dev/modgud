import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useSignalR } from '@/composables/useSignalR'
import type {
  OAuthClientDto,
  CreateOAuthClientDto,
  UpdateOAuthClientDto,
  OAuthClientListDto,
  OAuthClientCreatedDto,
  ClientSecretDto,
} from '@/models/oauth'
import type { TerminalOAuthAccessUpdateDto } from '@/models/position'

interface OAuthDataEvent {
  Subject: string
  Action: 'Created' | 'Updated' | 'Deleted' | 'Custom' | 'FullSync'
  Payload: unknown[]
}

/**
 * OAuth client store. Live-updated via the OAuthClientActions SignalR stream
 * (Created/Updated → upsert, Deleted → remove), so clients minted out-of-band —
 * DCR via /connect/register, another admin, another tab — appear without a
 * manual reload. The store also upserts after its own CUD calls so the grid
 * reflects a change immediately even before the SignalR echo arrives. The list
 * endpoint is paginated; we ask for a generous page size and paginate
 * client-side via the data grid.
 */
export const useOAuthClientStore = defineStore('oauth-client', () => {
  const http = useHttpClient('/api/admin/oauth/clients')
  const signalr = useSignalR()

  const clients = ref<OAuthClientDto[]>([])
  const loaded = ref(false)
  const totalCount = ref(0)
  let signalrSubscribed = false

  async function loadAll(): Promise<OAuthClientDto[]> {
    const res = await http
      .setQueryParameter('page', '1')
      .setQueryParameter('pageSize', '500')
      .get<OAuthClientListDto>()
    clients.value = res.Items
    totalCount.value = res.TotalCount
    loaded.value = true
    return res.Items
  }

  async function initialize() {
    // Live updates (DCR, other admins/tabs). The SignalR composable restores an
    // active stream after reconnect; only the REST re-sync must run again.
    if (!signalrSubscribed) {
      signalrSubscribed = true
      subscribeToSignalR()
      signalr.runOnReconnect(() => void loadAll(), 'OAuthClientActions.Reload')
    }

    if (!loaded.value) await loadAll()
  }

  function subscribeToSignalR() {
    signalr.stream<OAuthDataEvent>('OAuthClientActions.Subscribe').subscribe({
      next: (ev) => {
        if (ev.Action === 'Created' || ev.Action === 'Updated') {
          let next = clients.value
          for (const dto of ev.Payload as OAuthClientDto[]) next = upsert(next, dto)
          clients.value = next
        } else if (ev.Action === 'Deleted') {
          const ids = ev.Payload as string[]
          clients.value = clients.value.filter((c) => !ids.includes(c.Id))
        }
      },
      error: (err) => console.error('[oauth-client] SignalR stream error:', err),
    })
  }

  async function loadOne(id: string): Promise<OAuthClientDto | null> {
    try {
      const dto = await http.addPath(id).get<OAuthClientDto>()
      clients.value = upsert(clients.value, dto)
      return dto
    } catch {
      return null
    }
  }

  async function create(dto: CreateOAuthClientDto): Promise<OAuthClientCreatedDto> {
    const created = await http.post<OAuthClientCreatedDto>(dto)
    clients.value = upsert(clients.value, created.Client)
    return created
  }

  async function update(id: string, dto: UpdateOAuthClientDto): Promise<OAuthClientDto> {
    const updated = await http.addPath(id).put<OAuthClientDto>(dto)
    clients.value = upsert(clients.value, updated)
    return updated
  }

  async function updateTerminalAccess(
    clientId: string,
    terminalId: string,
    dto: TerminalOAuthAccessUpdateDto,
  ): Promise<OAuthClientDto> {
    const updated = await useHttpClient('/api/position-terminal')
      .addPath(terminalId, 'oauth-access')
      .put<OAuthClientDto>(dto)
    clients.value = upsert(clients.value, { ...updated, Id: updated.Id || clientId })
    return updated
  }

  async function remove(id: string): Promise<void> {
    await http.addPath(id).delete()
    clients.value = clients.value.filter((c) => c.Id !== id)
  }

  async function regenerateSecret(id: string): Promise<ClientSecretDto> {
    return await http.addPath(id, 'regenerate-secret').post<ClientSecretDto>({})
  }

  return {
    clients,
    loaded,
    totalCount,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    updateTerminalAccess,
    remove,
    regenerateSecret,
  }
})

function upsert(list: OAuthClientDto[], item: OAuthClientDto): OAuthClientDto[] {
  const idx = list.findIndex((c) => c.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
