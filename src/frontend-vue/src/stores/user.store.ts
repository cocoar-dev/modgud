import { ref } from 'vue'
import { defineStore } from 'pinia'
import { useEntityService } from '@/composables/useEntityService'
import { useHttpClient } from '@/composables/useHttpClient'
import type { UserDto, UserCreateDto, UserLookupDto } from '@/models/user'

export interface UserGroupDto {
  Id: string
  Name: string
  Description?: string | null
  IsAuto: boolean
}

export interface InheritedUserGroupDto extends UserGroupDto {
  ViaId: string
  ViaName: string
}

export interface UserGroupsDto {
  Direct: UserGroupDto[]
  Inherited: InheritedUserGroupDto[]
}

export type EffectiveGroupSource = 'DirectManual' | 'InheritedManual' | 'AutoMatched'

export interface EffectiveGroupRoleDto {
  Id: string
  Name: string
}

export interface EffectiveGroupViaStepDto {
  Id: string
  Name: string
}

export interface EffectiveGroupDto {
  Id: string
  Name: string
  Description: string | null
  Roles: EffectiveGroupRoleDto[]
  Source: EffectiveGroupSource
  Via?: EffectiveGroupViaStepDto[]
  /** AutoMatched only: true when the principal is also currently in MemberIds.
   *  False signals materialization drift — the script matches but the user is
   *  not in MemberIds, so a recompute would change the materialized state. */
  MaterializedMatches?: boolean
}

export interface EffectiveGroupDiagnostic {
  GroupId: string
  GroupName: string
  Kind: 'EvalFailed' | 'CompileFailed'
  Error: string
}

export interface EffectiveGroupsResponse {
  PrincipalId: string
  Groups: EffectiveGroupDto[]
  Diagnostics: EffectiveGroupDiagnostic[]
}

export const useUserStore = defineStore('user', () => {
  // Full entity service (admin-only: GET /api/user requires app:admin)
  const service = useEntityService<UserDto, UserCreateDto>({
    apiPath: '/api/user',
    entityName: 'User',
    enableSignalR: true,
    loadOnInit: false,
  })

  const http = useHttpClient('/api/user')

  // Lightweight lookup (any authenticated user)
  const lookupEntities = ref<UserLookupDto[]>([])
  let lookupLoaded = false

  async function loadLookup(): Promise<void> {
    if (lookupLoaded) return
    const data = await http.addPath('lookup').get<UserLookupDto[]>()
    lookupEntities.value = data.sort((a, b) => a.Label.localeCompare(b.Label))
    lookupLoaded = true
  }

  function getUserByAcronym(acronym: string): UserDto | undefined {
    return service.entities.value.find(u => u.Acronym === acronym)
  }

  async function setPassword(userId: string, password: string): Promise<void> {
    await http.addPath(userId, 'password').put({ Password: password })
  }

  async function setActive(userId: string, isActive: boolean): Promise<void> {
    await http.addPath(userId, 'active').put({ IsActive: isActive })
  }

  async function getGroups(userId: string): Promise<UserGroupsDto> {
    return await http.addPath(userId, 'groups').get<UserGroupsDto>()
  }

  /**
   * Admin debug surface: returns the live effective group membership of a user
   * — direct + inherited (manual) + auto-script matches — independent of
   * whether MemberIds is materialized. Used by the "Effektive Gruppen"
   * section in the Groups tab to surface materialization drift.
   */
  async function getEffectiveGroups(userId: string): Promise<EffectiveGroupsResponse> {
    return await http.addPath(userId, 'effective-groups').get<EffectiveGroupsResponse>()
  }

  async function addGroup(userId: string, groupId: string): Promise<void> {
    await http.addPath(userId, 'groups').post({ GroupId: groupId })
  }

  async function removeGroup(userId: string, groupId: string): Promise<void> {
    await http.addPath(userId, 'groups', groupId).delete()
  }

  // Admin security-info (2FA methods + grace due date). Requires app:admin.
  const adminHttp = useHttpClient('/api/admin/users')

  interface UserSecurityInfo {
    Has2FA: boolean
    TwoFactorMethods: string[]
    SecureSetupDueAt: string | null
    GracePeriodDaysOverride: number | null
    TwoFactorExempt: boolean
  }

  async function getSecurityInfo(userId: string): Promise<UserSecurityInfo> {
    return await adminHttp.addPath(userId, 'security-info').get<UserSecurityInfo>()
  }

  async function resetGrace(userId: string): Promise<string | null> {
    const result = await adminHttp.addPath(userId, 'grace', 'reset').post<{ SecureSetupDueAt: string | null }>()
    return result?.SecureSetupDueAt ?? null
  }

  async function clearGrace(userId: string): Promise<void> {
    await adminHttp.addPath(userId, 'grace').delete()
  }

  /**
   * Write per-user grace policy. Pass GracePeriodDaysOverride = -1 to clear the override
   * and fall back to the global default. Pass nulls to leave fields unchanged.
   */
  async function setGracePolicy(
    userId: string,
    policy: { GracePeriodDaysOverride?: number | null; TwoFactorExempt?: boolean | null },
  ): Promise<void> {
    await adminHttp.addPath(userId, 'grace', 'policy').put(policy)
  }

  return {
    ...service,
    lookupEntities,
    loadLookup,
    getUserByAcronym,
    setPassword,
    setActive,
    getGroups,
    getEffectiveGroups,
    addGroup,
    removeGroup,
    getSecurityInfo,
    resetGrace,
    clearGrace,
    setGracePolicy,
  }
})
