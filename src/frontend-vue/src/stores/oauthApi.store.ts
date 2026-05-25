import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  OAuthApiDto,
  OAuthApiListDto,
  OAuthApiCreatedDto,
  CreateOAuthApiDto,
  UpdateOAuthApiDto,
  OAuthScopeDto,
} from '@/models/oauth'

/**
 * OAuth API (resource server) store. A resource server has no credential
 * surface of its own — RS-to-IdP authentication runs through OAuth
 * (Client-Credentials with a linked ServiceAccount), so this store covers
 * only the RS metadata + implicit-scope companion.
 */
export const useOAuthApiStore = defineStore('oauth-api', () => {
  const http = useHttpClient('/api/admin/oauth/apis')

  const apis = ref<OAuthApiDto[]>([])
  const loaded = ref(false)
  const totalCount = ref(0)

  async function loadAll(): Promise<OAuthApiDto[]> {
    const res = await http
      .setQueryParameter('page', '1')
      .setQueryParameter('pageSize', '500')
      .get<OAuthApiListDto>()
    apis.value = res.Items
    totalCount.value = res.TotalCount
    loaded.value = true
    return res.Items
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
  }

  async function loadOne(id: string): Promise<OAuthApiDto | null> {
    try {
      const dto = await http.addPath(id).get<OAuthApiDto>()
      apis.value = upsert(apis.value, dto)
      return dto
    } catch {
      return null
    }
  }

  async function create(dto: CreateOAuthApiDto): Promise<OAuthApiCreatedDto> {
    const created = await http.post<OAuthApiCreatedDto>(dto)
    await loadOne(created.Id)
    return created
  }

  async function update(id: string, dto: UpdateOAuthApiDto): Promise<OAuthApiDto> {
    const updated = await http.addPath(id).put<OAuthApiDto>(dto)
    apis.value = upsert(apis.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await http.addPath(id).delete()
    apis.value = apis.value.filter((a) => a.Id !== id)
  }

  /**
   * Creates the 1:1 companion OAuthScope for an existing API. Eliminates
   * the manual two-step "create API + create matching scope" flow. The
   * server-side mints a scope with name=api.Name, Resources=[api.Name],
   * ShowInDiscoveryDocument=false; the API's HasImplicitScope flag flips
   * to true after a reload.
   */
  async function createImplicitScope(id: string): Promise<OAuthScopeDto> {
    const created = await http.addPath(id, 'create-implicit-scope').post<OAuthScopeDto>({})
    await loadOne(id)
    return created
  }

  return {
    apis,
    loaded,
    totalCount,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    remove,
    createImplicitScope,
  }
})

function upsert(list: OAuthApiDto[], item: OAuthApiDto): OAuthApiDto[] {
  const idx = list.findIndex((a) => a.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
