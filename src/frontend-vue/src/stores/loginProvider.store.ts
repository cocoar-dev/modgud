import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  LoginProviderDto,
  CreateLoginProviderDto,
  UpdateLoginProviderDto,
  LoginProviderListDto,
} from '@/models/loginProvider'

export const useLoginProviderStore = defineStore('login-provider', () => {
  const http = useHttpClient('/api/admin/login-providers')

  const providers = ref<LoginProviderDto[]>([])
  const loaded = ref(false)

  async function loadAll(): Promise<LoginProviderDto[]> {
    const res = await http.get<LoginProviderListDto>()
    providers.value = res.Items
    loaded.value = true
    return res.Items
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
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

  async function create(dto: CreateLoginProviderDto): Promise<LoginProviderDto> {
    const created = await http.post<LoginProviderDto>(dto)
    providers.value = upsert(providers.value, created)
    return created
  }

  async function update(id: string, dto: UpdateLoginProviderDto): Promise<LoginProviderDto> {
    const updated = await http.addPath(id).patch<LoginProviderDto>(dto)
    providers.value = upsert(providers.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await http.addPath(id).delete()
    providers.value = providers.value.filter((p) => p.Id !== id)
  }

  return {
    providers,
    loaded,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    remove,
  }
})

function upsert(list: LoginProviderDto[], item: LoginProviderDto): LoginProviderDto[] {
  const idx = list.findIndex((p) => p.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
