import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  RealmDto,
  CreateRealmDto,
  UpdateRealmDto,
  RealmListDto,
} from '@/models/realm'

/**
 * Realm store. Realms are addressed by Slug (not Id) on the API side, so
 * the keyed lookups below use Slug. Only realms with `CanManageTenants`
 * can call this endpoint — the backend returns 404 otherwise.
 */
export const useRealmStore = defineStore('realm', () => {
  const http = useHttpClient('/api/admin/realms')

  const realms = ref<RealmDto[]>([])
  const loaded = ref(false)

  async function loadAll(): Promise<RealmDto[]> {
    const res = await http.get<RealmListDto>()
    realms.value = res.Items
    loaded.value = true
    return res.Items
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
  }

  async function loadOne(slug: string): Promise<RealmDto | null> {
    try {
      const dto = await http.addPath(slug).get<RealmDto>()
      realms.value = upsert(realms.value, dto)
      return dto
    } catch {
      return null
    }
  }

  async function create(dto: CreateRealmDto): Promise<RealmDto> {
    const created = await http.post<RealmDto>(dto)
    realms.value = upsert(realms.value, created)
    return created
  }

  async function update(slug: string, dto: UpdateRealmDto): Promise<RealmDto> {
    const updated = await http.addPath(slug).patch<RealmDto>(dto)
    realms.value = upsert(realms.value, updated)
    return updated
  }

  async function remove(slug: string): Promise<void> {
    await http.addPath(slug).delete()
    realms.value = realms.value.filter((r) => r.Slug !== slug)
  }

  return {
    realms,
    loaded,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    remove,
  }
})

function upsert(list: RealmDto[], item: RealmDto): RealmDto[] {
  const idx = list.findIndex((r) => r.Slug === item.Slug)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
