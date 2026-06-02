import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type {
  RealmDto,
  CreateRealmDto,
  CreatedRealmDto,
  InitialAdminInviteDto,
  UpdateRealmDto,
  RealmListDto,
} from '@/models/realm'

/**
 * Realm store. Realms are addressed by Slug (not Id) on the API side, so
 * the keyed lookups below use Slug. Only the Control-Plane realm can call
 * this endpoint — the backend returns 404 otherwise (see
 * RequireControlPlaneFilter + ControlPlaneGateMiddleware).
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

  async function create(dto: CreateRealmDto): Promise<CreatedRealmDto> {
    // Response shape changed in C15c: {Realm, InitialAdminInvite}.
    const created = await http.post<CreatedRealmDto>(dto)
    realms.value = upsert(realms.value, created.Realm)
    return created
  }

  async function resendBootstrapInvite(slug: string): Promise<InitialAdminInviteDto> {
    return await http.addPath(slug).addPath('resend-bootstrap-invite').post<InitialAdminInviteDto>({})
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

  async function transferControlPlane(slug: string): Promise<RealmDto> {
    // POST to the realm that should BECOME the control plane. The current host
    // (the present control plane) loses the realm-management surface afterwards,
    // so we deliberately do NOT reload the list here — a follow-up call would
    // 404. The caller shows a terminal "moved" state instead.
    return await http.addPath(slug).addPath('transfer-control-plane').post<RealmDto>({})
  }

  return {
    realms,
    loaded,
    initialize,
    loadAll,
    loadOne,
    create,
    resendBootstrapInvite,
    update,
    remove,
    transferControlPlane,
  }
})

function upsert(list: RealmDto[], item: RealmDto): RealmDto[] {
  const idx = list.findIndex((r) => r.Slug === item.Slug)
  if (idx < 0) return [...list, item]
  const copy = [...list]
  copy[idx] = item
  return copy
}
