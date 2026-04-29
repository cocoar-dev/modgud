import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  OAuthScopeDto,
  CreateOAuthScopeDto,
  UpdateOAuthScopeDto,
} from '@/models/oauth'

/**
 * OAuth scope store. The list endpoint returns a flat array (no pagination).
 */
export const useOAuthScopeStore = defineStore('oauth-scope', () => {
  const http = useHttpClient('/api/admin/oauth/scopes')

  const scopes = ref<OAuthScopeDto[]>([])
  const loaded = ref(false)

  async function loadAll(): Promise<OAuthScopeDto[]> {
    scopes.value = await http.get<OAuthScopeDto[]>()
    loaded.value = true
    return scopes.value
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
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
