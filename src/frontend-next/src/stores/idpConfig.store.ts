import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  IdpConfigDto,
  FlavorDto,
  UpdateIdpConfigRequest,
  CreateIdpConfigRequest,
  TestUserUpdateRequest,
  TestUserUpdateResponse,
} from '@/models/idpConfig'

/**
 * Pinia store for IdP configurations (admin-only). Deliberately does NOT use
 * useEntityService because the secret-rotation + enable/disable endpoints
 * don't match the generic CRUD shape.
 */
export const useIdpConfigStore = defineStore('idpConfig', () => {
  const http = useHttpClient('/api/admin/idp-config')

  const configs = ref<IdpConfigDto[]>([])
  const flavors = ref<FlavorDto[]>([])
  const loaded = ref(false)
  const flavorsLoaded = ref(false)

  async function loadAll() {
    configs.value = await http.get<IdpConfigDto[]>()
    loaded.value = true
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
    if (!flavorsLoaded.value) {
      flavors.value = await http.addPath('flavors').get<FlavorDto[]>()
      flavorsLoaded.value = true
    }
  }

  async function loadOne(id: string): Promise<IdpConfigDto | null> {
    try {
      const c = await http.addPath(id).get<IdpConfigDto>()
      configs.value = upsert(configs.value, c)
      return c
    } catch {
      return null
    }
  }

  async function create(dto: CreateIdpConfigRequest): Promise<IdpConfigDto> {
    const created = await http.post<IdpConfigDto>(dto)
    configs.value = [...configs.value, created]
    return created
  }

  async function update(id: string, dto: UpdateIdpConfigRequest): Promise<IdpConfigDto> {
    const updated = await http.addPath(id).put<IdpConfigDto>(dto)
    configs.value = upsert(configs.value, updated)
    return updated
  }

  async function enable(id: string): Promise<IdpConfigDto> {
    const updated = await http.addPath(id).addPath('enable').post<IdpConfigDto>({})
    configs.value = upsert(configs.value, updated)
    return updated
  }

  async function disable(id: string): Promise<IdpConfigDto> {
    const updated = await http.addPath(id).addPath('disable').post<IdpConfigDto>({})
    configs.value = upsert(configs.value, updated)
    return updated
  }

  async function rotateSecret(id: string, secret: string): Promise<void> {
    await http.addPath(id).addPath('secret').post({ Secret: secret })
    await loadOne(id)
  }

  async function remove(id: string): Promise<void> {
    await http.addPath(id).delete()
    configs.value = configs.value.filter((c) => c.Id !== id)
  }

  async function testUserUpdate(id: string, request: TestUserUpdateRequest): Promise<TestUserUpdateResponse> {
    return await http.addPath(id).addPath('test-user-update').post<TestUserUpdateResponse>(request)
  }

  async function getLastRawClaims(id: string): Promise<unknown | null> {
    try {
      const res = await http.addPath(id).addPath('last-raw-claims').get<{ Available: boolean; RawClaims?: unknown }>()
      return res.Available ? res.RawClaims : null
    } catch {
      return null
    }
  }

  return {
    configs,
    flavors,
    loaded,
    flavorsLoaded,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    enable,
    disable,
    rotateSecret,
    remove,
    testUserUpdate,
    getLastRawClaims,
  }
})

function upsert(list: IdpConfigDto[], item: IdpConfigDto): IdpConfigDto[] {
  const idx = list.findIndex((c) => c.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
