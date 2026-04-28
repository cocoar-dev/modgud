import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type { RoleDto } from '@/models/role'

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

  async function createRole(dto: { Name: string; Description?: string; ResourceType: string; Permissions: string[] }): Promise<RoleDto> {
    const role = await http.post<RoleDto>(dto)
    roles.value = [...roles.value, role]
    return role
  }

  async function updateRole(id: string, dto: { Name: string; Description?: string; ResourceType: string; Permissions: string[] }): Promise<RoleDto> {
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
    createRole,
    updateRole,
    deleteRole,
  }
})
