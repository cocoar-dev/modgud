import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'

export type MembershipMode = 'Manual' | 'Dynamic'
export type EmailMode = 'Shared' | 'Broadcast'

export interface ResourceAccessScriptDto {
  ResourceType: string
  Script?: string | null
}

/**
 * Authorization group DTO returned by `/api/admin/authorization-groups`.
 * Mirrors `Cocoar.Auth.Application.DTOs.Authorization.AuthorizationGroupDto`.
 */
export interface AuthorizationGroupDto {
  Id: string
  Name: string
  Description?: string | null
  MemberIds: string[]
  RoleIds: string[]
  AccessScripts: ResourceAccessScriptDto[]
  MembershipMode: MembershipMode
  MembershipScript?: string | null
  MembershipScriptDependencies?: string[] | null
  MembershipLastError?: string | null
  Email?: string | null
  EmailMode: EmailMode
}

export interface CreateAuthorizationGroupInput {
  Name: string
  Description?: string
  MemberIds?: string[]
  RoleIds?: string[]
  AccessScripts?: ResourceAccessScriptDto[]
  MembershipMode?: MembershipMode
  MembershipScript?: string
  Email?: string
  EmailMode?: EmailMode
}

export interface UpdateAuthorizationGroupInput extends CreateAuthorizationGroupInput {}

/**
 * Wraps `useEntityService` for the /api/admin/authorization-groups endpoint.
 * SignalR subject name is `AuthorizationGroup`, matching the backend hub events.
 */
export const useAuthorizationGroupStore = defineStore('authorization-group', () => {
  const service = useEntityService<AuthorizationGroupDto, CreateAuthorizationGroupInput>({
    apiPath: '/api/admin/authorization-groups',
    entityName: 'AuthorizationGroup',
    enableSignalR: true,
    loadOnInit: false,
  })

  return service
})
