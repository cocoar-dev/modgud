import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  OAuthClientDto,
  CreateOAuthClientDto,
  UpdateOAuthClientDto,
  OAuthClientListDto,
  OAuthClientCreatedDto,
  ClientSecretDto,
} from '@/models/oauth'

/**
 * OAuth client store. Backend has no SignalR stream for OAuth — we keep
 * a manually-managed list and refresh after CUD operations. The list endpoint
 * is paginated; we ask for a generous page size and only paginate client-side
 * via the data grid.
 */
export const useOAuthClientStore = defineStore('oauth-client', () => {
  const http = useHttpClient('/api/admin/oauth/clients')

  const clients = ref<OAuthClientDto[]>([])
  const loaded = ref(false)
  const totalCount = ref(0)

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
    if (!loaded.value) await loadAll()
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
