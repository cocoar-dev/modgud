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
  Email?: string
  EmailMode?: 'Shared' | 'ExpandToMembers'
  /**
   * App slugs the group is active in. On create, omitting falls back to
   * the backend default (currently [cocoar-auth]). On update, omitting
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
    createGroup,
    updateGroup,
    deleteGroup,
    getEffectiveMembers,
  }
})
