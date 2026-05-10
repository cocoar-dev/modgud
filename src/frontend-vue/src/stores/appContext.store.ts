import { defineStore } from 'pinia'
import { computed, ref, watch } from 'vue'

/**
 * Active "App workspace" the admin is operating in. Drives the filter on
 * Scopes / APIs / Clients / Roles / Groups grids so the admin can focus
 * on a single application instead of scrolling a flat realm-wide list.
 *
 * <para>Three sentinel values for the selector:</para>
 * <list type="bullet">
 *   <item><c>'all'</c> — unfiltered, default. Same view as before this
 *   feature existed.</item>
 *   <item><c>'global'</c> — only realm-wide entries (no AppId). The
 *   five OIDC standard scopes, the System-Admin role, etc.</item>
 *   <item>An <c>App.Id</c> (Guid string) — strict filter to that app's
 *   entries. Globals are NOT merged in; pick <c>'all'</c> if you want
 *   to see them alongside.</item>
 * </list>
 *
 * <para>Persisted to localStorage so refresh / tab-switch keeps the
 * chosen workspace.</para>
 */
export type AppContextSelection = 'all' | 'global' | string

const STORAGE_KEY = 'cocoar.admin.appContext'

function loadInitial(): AppContextSelection {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    return raw ?? 'all'
  } catch {
    return 'all'
  }
}

export const useAppContextStore = defineStore('app-context', () => {
  const selection = ref<AppContextSelection>(loadInitial())

  watch(selection, (v) => {
    try { localStorage.setItem(STORAGE_KEY, v) } catch { /* private mode etc — ignore */ }
  })

  const isFiltered = computed(() => selection.value !== 'all')
  const isGlobalOnly = computed(() => selection.value === 'global')
  /** Returns the App.Id string when a specific app is selected, otherwise null. */
  const selectedAppId = computed<string | null>(() =>
    selection.value === 'all' || selection.value === 'global' ? null : selection.value)

  /**
   * Filter for entries that have a single optional <c>AppId</c>
   * (Scopes, APIs, Roles). Returns true if the entry should be shown
   * given the current selection.
   */
  function matchesSingleAppId(entryAppId: string | null | undefined): boolean {
    if (selection.value === 'all') return true
    if (selection.value === 'global') return !entryAppId
    return entryAppId === selection.value
  }

  /**
   * Filter for entries with a many-valued <c>AppIds</c> list (Clients).
   * Realm-wide = empty list.
   */
  function matchesAppIdList(entryAppIds: readonly string[] | null | undefined): boolean {
    if (selection.value === 'all') return true
    const ids = entryAppIds ?? []
    if (selection.value === 'global') return ids.length === 0
    return ids.includes(selection.value)
  }

  /**
   * Filter for Groups whose <c>BoundTo</c> is a list of App slugs (with
   * '*' wildcard meaning "every app"). The selector value is an App.Id
   * though, not a slug, so the caller passes both the entry's BoundTo
   * AND the selected App's slug for comparison.
   */
  function matchesBoundToSlugs(boundTo: readonly string[] | null | undefined, selectedAppSlug: string | null): boolean {
    if (selection.value === 'all') return true
    const list = boundTo ?? []
    if (selection.value === 'global') return list.length === 0
    if (!selectedAppSlug) return false
    return list.includes('*') || list.includes(selectedAppSlug)
  }

  function set(value: AppContextSelection) {
    selection.value = value
  }

  return {
    selection,
    isFiltered,
    isGlobalOnly,
    selectedAppId,
    set,
    matchesSingleAppId,
    matchesAppIdList,
    matchesBoundToSlugs,
  }
})
