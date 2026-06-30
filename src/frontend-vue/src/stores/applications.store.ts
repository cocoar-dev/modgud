import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  ApplicationDto,
  CreateApplicationDto,
  UpdateApplicationDto,
} from '@/models/application'

/**
 * Applications store — the realm's registered Cocoar SaaS apps. The system
 * app (`modgud`) is always present and cannot be deleted.
 *
 * Distinct from the existing `useAppStore` (UI shell state for the
 * header/footer/content layout) — that one keeps its name; this one is
 * about the admin-managed Application records.
 */
export const useApplicationsStore = defineStore('applications', () => {
  const http = useHttpClient('/api/app')

  const apps = ref<ApplicationDto[]>([])
  const loaded = ref(false)

  async function loadAll(): Promise<ApplicationDto[]> {
    const res = await http.get<ApplicationDto[]>()
    apps.value = res
    loaded.value = true
    return res
  }

  async function initialize() {
    if (!loaded.value) await loadAll()
  }

  async function loadOne(id: string): Promise<ApplicationDto | null> {
    try {
      const dto = await http.addPath(id).get<ApplicationDto>()
      apps.value = upsert(apps.value, dto)
      return dto
    } catch {
      return null
    }
  }

  async function create(dto: CreateApplicationDto): Promise<ApplicationDto> {
    const created = await http.post<ApplicationDto>(dto)
    apps.value = upsert(apps.value, created)
    return created
  }

  async function update(id: string, dto: UpdateApplicationDto): Promise<ApplicationDto> {
    const updated = await http.addPath(id).put<ApplicationDto>(dto)
    apps.value = upsert(apps.value, updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await http.addPath(id).delete()
    apps.value = apps.value.filter((a) => a.Id !== id)
  }

  // The per-App ADR-0011 settings override rides inline on loadOne/create/update
  // (an App is one resource) — there is no separate settings endpoint.

  return {
    apps,
    loaded,
    initialize,
    loadAll,
    loadOne,
    create,
    update,
    remove,
  }
})

function upsert(list: ApplicationDto[], item: ApplicationDto): ApplicationDto[] {
  const idx = list.findIndex((a) => a.Id === item.Id)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
