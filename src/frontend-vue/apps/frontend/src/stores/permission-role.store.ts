import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'

/**
 * Permission role DTO returned by `/api/admin/permission-roles`.
 * Mirrors `Cocoar.Auth.Application.DTOs.Authorization.PermissionRoleDto`.
 */
export interface PermissionRoleDto {
  Id: string
  Name: string
  Description?: string | null
  ResourceType: string
  Permissions: string[]
}

export interface CreatePermissionRoleInput {
  Name: string
  Description?: string
  ResourceType: string
  Permissions: string[]
}

export interface UpdatePermissionRoleInput {
  Name: string
  Description?: string
  ResourceType: string
  Permissions: string[]
}

/**
 * Wraps `useEntityService` for the /api/admin/permission-roles endpoint.
 * SignalR subject name is `PermissionRole`, matching the backend hub events.
 */
export const usePermissionRoleStore = defineStore('permission-role', () => {
  const service = useEntityService<PermissionRoleDto, CreatePermissionRoleInput>({
    apiPath: '/api/admin/permission-roles',
    entityName: 'PermissionRole',
    enableSignalR: true,
    loadOnInit: false,
  })

  return service
})
