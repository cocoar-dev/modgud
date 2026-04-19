import { defineStore } from 'pinia'
import { ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'

/**
 * Cross-type principal lookup DTO returned by `/api/admin/principals/search`.
 * Mirrors `Cocoar.Auth.Api.Controllers.Admin.PrincipalLookupDto`.
 */
export interface PrincipalLookupDto {
  Id: string
  Type: string
  DisplayLabel: string
  Email?: string | null
  IsActive: boolean
  CanAuthenticate: boolean
  IsContainer: boolean
}

/**
 * Principal store — backs the member-picker in the AuthorizationGroup UI.
 * The backend only exposes `/search` and `/{id}` (no list endpoint), so this
 * store does not use `useEntityService`. Results are cached by id in a map
 * to support O(1) label lookups after initial search.
 */
export const usePrincipalStore = defineStore('principal', () => {
  const http = useHttpClient('/api/admin/principals')

  const cache = ref(new Map<string, PrincipalLookupDto>())
  const lastResults = ref<PrincipalLookupDto[]>([])

  /**
   * Search principals by display label (case-insensitive contains).
   * Results are capped at 50 by the backend.
   */
  async function search(q?: string, type?: 'Person' | 'Group'): Promise<PrincipalLookupDto[]> {
    const results = await http
      .addPath('search')
      .setOptionalQueryParameter('q', q)
      .setOptionalQueryParameter('type', type)
      .get<PrincipalLookupDto[]>()

    const next = new Map(cache.value)
    for (const p of results) next.set(p.Id, p)
    cache.value = next
    lastResults.value = results
    return results
  }

  /**
   * Look up a principal by id. Returns from the cache when available,
   * otherwise fetches from the backend.
   */
  async function getById(id: string): Promise<PrincipalLookupDto | null> {
    const cached = cache.value.get(id)
    if (cached) return cached
    try {
      const dto = await http.addPath(id).get<PrincipalLookupDto>()
      const next = new Map(cache.value)
      next.set(id, dto)
      cache.value = next
      return dto
    } catch {
      return null
    }
  }

  function findInCache(id: string): PrincipalLookupDto | undefined {
    return cache.value.get(id)
  }

  return {
    cache,
    lastResults,
    search,
    getById,
    findInCache,
  }
})
