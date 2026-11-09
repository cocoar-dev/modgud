import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type { RoleDto, RolePayload } from '@/models/role'

export const useRoleStore = defineStore('role', () => {
  const http = useHttpClient('/api/role')
  const roles = ref<RoleDto[]>([])
  const loaded = ref(false)

  async function loadAll() {
    roles.value = await http.get<RoleDto[]>()
    loaded.value = true
  }

  async function initialize() {
    if (!loaded.value) {
      await loadAll()
    }
  }

  /** D.roles-F02 — the current server state of one role, refreshing the cached entry.
   * The editor must start from it: the list cache is loaded once per SPA session and
   * never refreshed, and saving a stale permission set restores grants revoked since. */
  async function fetchRole(id: string): Promise<RoleDto> {
    const role = await http.addPath(id).get<RoleDto>()
    roles.value = roles.value.some(r => r.Id === role.Id)
      ? roles.value.map(r => r.Id === role.Id ? role : r)
      : [...roles.value, role]
    return role
  }

  async function createRole(dto: RolePayload): Promise<RoleDto> {
    const role = await http.post<RoleDto>(dto)
    roles.value = [...roles.value, role]
    return role
  }

  async function updateRole(id: string, dto: RolePayload): Promise<RoleDto> {
    const role = await http.addPath(id).put<RoleDto>(dto)
    roles.value = roles.value.map(r => r.Id === id ? role : r)
    return role
  }

  async function deleteRole(id: string): Promise<void> {
    await http.addPath(id).delete()
    roles.value = roles.value.filter(r => r.Id !== id)
  }

  return {
    roles,
    loaded,
    loadAll,
    initialize,
    fetchRole,
    createRole,
    updateRole,
    deleteRole,
  }
})
