import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  LoginProviderDto,
  FlavorDto,
  CreateLoginProviderRequest,
  UpdateLoginProviderRequest,
  TestUserUpdateRequest,
  TestUserUpdateResponse,
} from '@/models/loginProvider'

/**
 * Pinia store for login providers (admin-only). Deliberately does NOT use
 * useEntityService because the secret-rotation, enable/disable, and
 * test-user-update endpoints don't fit the generic CRUD shape.
 *
 * Replaces the legacy `idpConfig` store. Endpoint group:
 * `/api/admin/login-providers/*`. The Internal seed surfaces as a regular
 * entry with `IsBuiltIn = true` so the UI can lock it.
 */
export const useLoginProviderStore = defineStore('login-provider', () => {
  const http = useHttpClient('/api/admin/login-providers')

  const providers = ref<LoginProviderDto[]>([])
  const flavors = ref<FlavorDto[]>([])
  const loaded = ref(false)
  const flavorsLoaded = ref(false)

  async function loadAll(): Promise<LoginProviderDto[]> {
    providers.value = await http.get<LoginProviderDto[]>()
    loaded.value = true
    return providers.value
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
    if (!flavorsLoaded.value) {
      flavors.value = await http.addPath('flavors').get<FlavorDto[]>()
      flavorsLoaded.value = true
    }
  }

  async function loadOne(id: string): Promise<LoginProviderDto | null> {
    try {
      const dto = await http.addPath(id).get<LoginProviderDto>()
      providers.value = upsert(providers.value, dto)
      return dto
    } catch {
      return null
    }
  }

  async function create(dto: CreateLoginProviderRequest): Promise<LoginProviderDto> {
    const created = await http.post<LoginProviderDto>(dto)
    providers.value = upsert(providers.value, created)
    return created
  }

  async function update(id: string, dto: UpdateLoginProviderRequest): Promise<LoginProviderDto> {
    const updated = await http.addPath(id).put<LoginProviderDto>(dto)
    providers.value = upsert(providers.value, updated)
    return updated
  }

  // Enable/disable is a partial update (PATCH) of just the Enabled property —
  // no dedicated endpoint. Used by the grid's inline toggle; the edit modal
  // stages Enabled in its form and sends it with the full save instead.
  async function setEnabled(id: string, enabled: boolean): Promise<LoginProviderDto> {
    const updated = await http.addPath(id).put<LoginProviderDto>({ Enabled: enabled })
    providers.value = upsert(providers.value, updated)
    return updated
  }

  async function rotateSecret(id: string, secret: string): Promise<void> {
    await http.addPath(id).addPath('secret').post({ Secret: secret })
    await loadOne(id)
  }

  async function remove(id: string): Promise<void> {
    await http.addPath(id).delete()
    providers.value = providers.value.filter((p) => p.Id !== id)
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

  /** True when the provider with the given id is the seeded built-in entry. */
  function isBuiltIn(id: string): boolean {
    return providers.value.find((p) => p.Id === id)?.IsBuiltIn === true
  }

  return {
    providers,
    flavors,
    loaded,
    flavorsLoaded,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    setEnabled,
    rotateSecret,
    remove,
    testUserUpdate,
    getLastRawClaims,
    isBuiltIn,
  }
})

function upsert(list: LoginProviderDto[], item: LoginProviderDto): LoginProviderDto[] {
  const idx = list.findIndex((p) => p.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
