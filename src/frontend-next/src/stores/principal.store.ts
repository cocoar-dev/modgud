import { ref } from 'vue'
import { defineStore } from 'pinia'
import { useHttpClient } from '@/composables/useHttpClient'
import type { PrincipalType } from '@/models/common'

export interface PrincipalLookupDto {
  Id: string
  Label: string
  Type: PrincipalType
  UserName: string | null
  Firstname: string | null
  Lastname: string | null
  Acronym: string | null
  Description: string | null
  Email: string | null
}

/**
 * Cross-type principal lookup for assignee pickers (Todos, Group-members, etc).
 * Returns active Persons and Groups in a unified list — the Type field lets the UI
 * render appropriate icons and group options.
 */
export const usePrincipalStore = defineStore('principal', () => {
  const http = useHttpClient('/api/principal')

  const lookupEntities = ref<PrincipalLookupDto[]>([])
  let lookupLoaded = false

  async function loadLookup(): Promise<void> {
    if (lookupLoaded) return
    const data = await http.addPath('lookup').get<PrincipalLookupDto[]>()
    lookupEntities.value = data
    lookupLoaded = true
  }

  /** Force-refresh the lookup on demand (after a group create/update, etc). */
  async function refreshLookup(): Promise<void> {
    const data = await http.addPath('lookup').get<PrincipalLookupDto[]>()
    lookupEntities.value = data
    lookupLoaded = true
  }

  function findById(id: string): PrincipalLookupDto | undefined {
    return lookupEntities.value.find(p => p.Id === id)
  }

  return {
    lookupEntities,
    loadLookup,
    refreshLookup,
    findById,
  }
})
