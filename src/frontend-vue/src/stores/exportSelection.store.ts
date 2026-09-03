import { computed, ref, watch } from 'vue'
import { defineStore } from 'pinia'

/**
 * The export selection ("cart") — entities collected from the normal admin
 * lists for a selective manifest export. The admin browses the grids with
 * their full search/filter power (a prod realm can hold thousands of
 * objects), adds rows via the context menu, and a footer bar offers the
 * export once the selection is non-empty.
 *
 * Entries are (section, naturalKey) pairs — the same addressing the
 * manifest / selective-export closure uses. Persisted per browser
 * (localStorage) so the selection survives navigation and reloads; it never
 * leaves the browser until the admin downloads the manifest.
 */

export interface ExportSelectionItem {
  section: string
  key: string
}

const STORAGE_KEY = 'modgud-export-selection'

function load(): ExportSelectionItem[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter((x): x is ExportSelectionItem =>
      !!x && typeof x.section === 'string' && typeof x.key === 'string')
  } catch {
    return []
  }
}

export const useExportSelectionStore = defineStore('exportSelection', () => {
  const items = ref<ExportSelectionItem[]>(load())

  watch(items, (value) => {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(value)) } catch { /* ignore */ }
  }, { deep: true })

  const count = computed(() => items.value.length)

  function has(section: string, key: string): boolean {
    return items.value.some((i) => i.section === section && i.key === key)
  }

  function add(section: string, key: string) {
    if (!key || has(section, key)) return
    items.value = [...items.value, { section, key }]
  }

  function remove(section: string, key: string) {
    items.value = items.value.filter((i) => !(i.section === section && i.key === key))
  }

  function toggle(section: string, key: string) {
    if (has(section, key)) remove(section, key)
    else add(section, key)
  }

  function clear() {
    items.value = []
  }

  /** Selection keys in the `${section}/${key}` shape the selective-export
   * closure logic uses. */
  const selectionKeys = computed(() => items.value.map((i) => `${i.section}/${i.key}`))

  return { items, count, has, add, remove, toggle, clear, selectionKeys }
})
