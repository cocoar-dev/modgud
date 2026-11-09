import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import type { GroupDto, MembershipMode } from '@/models/group'
import type { PrincipalType } from '@/models/common'

export interface EffectiveMemberDto {
  Id: string
  Label: string
  Type: PrincipalType
  UserName: string | null
  Firstname: string | null
  Lastname: string | null
  Acronym: string | null
  Description: string | null
  ViaId?: string | null
  ViaName?: string | null
}

export interface EffectiveMembersDto {
  Direct: EffectiveMemberDto[]
  Nested: EffectiveMemberDto[]
}

interface GroupPayload {
  Name: string
  Description?: string
  MemberIds: string[]
  RoleIds: string[]
  MembershipMode: MembershipMode
  MembershipScript?: string
  /** Federation v1: opt this Auto group into login-time externally-derived membership. */
  ExternallyDrivable?: boolean
  ExcludeTestAccounts?: boolean
  Email?: string
  EmailMode?: 'Shared' | 'ExpandToMembers'
  /**
   * App slugs the group is active in. On create, omitting falls back to
   * the backend default (currently [modgud]). On update, omitting
   * keeps the stored value untouched.
   */
  BoundTo?: string[]
}

export const useGroupStore = defineStore('group', () => {
  const http = useHttpClient('/api/group')
  const groups = ref<GroupDto[]>([])
  const loaded = ref(false)

  async function loadAll() {
    groups.value = await http.get<GroupDto[]>()
    loaded.value = true
  }

  async function initialize() {
    if (!loaded.value) {
      await loadAll()
    }
  }

  /** B3-F04 — the current server state of one group, refreshing the cached entry. The
   * editor must start from it: the list cache is loaded once per SPA session and never
   * refreshed, and saving stale member/role lists restores what was removed since. */
  async function fetchGroup(id: string): Promise<GroupDto> {
    const group = await http.addPath(id).get<GroupDto>()
    groups.value = groups.value.some(g => g.Id === group.Id)
      ? groups.value.map(g => g.Id === group.Id ? group : g)
      : [...groups.value, group]
    return group
  }

  async function createGroup(dto: GroupPayload): Promise<GroupDto> {
    const group = await http.post<GroupDto>(dto)
    groups.value = [...groups.value, group]
    return group
  }

  async function updateGroup(id: string, dto: GroupPayload): Promise<GroupDto> {
    const group = await http.addPath(id).put<GroupDto>(dto)
    groups.value = groups.value.map(g => g.Id === id ? group : g)
    return group
  }

  async function deleteGroup(id: string): Promise<void> {
    await http.addPath(id).delete()
    groups.value = groups.value.filter(g => g.Id !== id)
  }

  async function getEffectiveMembers(id: string): Promise<EffectiveMembersDto> {
    return await http.addPath(id, 'effective-members').get<EffectiveMembersDto>()
  }

  return {
    groups,
    loaded,
    loadAll,
    initialize,
    fetchGroup,
    createGroup,
    updateGroup,
    deleteGroup,
    getEffectiveMembers,
  }
})
