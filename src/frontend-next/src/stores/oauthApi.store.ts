import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  OAuthApiDto,
  OAuthApiListDto,
  OAuthApiCreatedDto,
  CreateOAuthApiDto,
  UpdateOAuthApiDto,
  CreateApiSecretDto,
  ApiSecretCreatedDto,
} from '@/models/oauth'

/**
 * OAuth API (resource server) store. Supports multiple secrets per API —
 * the create-secret endpoint returns the cleartext secret one time only.
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
    // Created DTO doesn't include `Secrets` — refresh that single record so
    // the secret list shows the newly minted entry without an extra round-trip.
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

  async function regenerateSecret(id: string): Promise<{ ApiSecret: string }> {
    const res = await http.addPath(id, 'regenerate-secret').post<{ ApiSecret: string }>({})
    await loadOne(id)
    return res
  }

  async function createSecret(id: string, dto: CreateApiSecretDto): Promise<ApiSecretCreatedDto> {
    const created = await http.addPath(id, 'secrets').post<ApiSecretCreatedDto>(dto)
    await loadOne(id)
    return created
  }

  async function deleteSecret(id: string, secretId: string): Promise<void> {
    await http.addPath(id, 'secrets', secretId).delete()
    await loadOne(id)
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
    regenerateSecret,
    createSecret,
    deleteSecret,
  }
})

function upsert(list: OAuthApiDto[], item: OAuthApiDto): OAuthApiDto[] {
  const idx = list.findIndex((a) => a.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
