import { computed, type ComputedRef, type Ref } from 'vue'
import { useI18n } from '@cocoar/vue-localization'
import { useExportSelectionStore } from '@/stores/exportSelection.store'

/**
 * Context-menu wiring for the export selection: every admin grid offers
 * "Add to export selection" on its rows, so collecting entities for a
 * selective manifest export happens right where the search/filter power is —
 * a prod realm can hold thousands of objects. The footer
 * ExportSelectionBar picks the collection up.
 *
 * `exportKey` yields the selected row's manifest natural key, or null when
 * the row isn't exportable (system/standard/built-in entities, SA-linked or
 * terminal-managed clients, draft-only creations — none of them appear in
 * the export).
 */
export function useExportSelectionMenu(section: string, exportKey: Ref<string | null> | ComputedRef<string | null>) {
  const store = useExportSelectionStore()
  const { t } = useI18n()

  const exportMenuVisible = computed(() => exportKey.value !== null)
  const exportMenuLabel = computed(() =>
    exportKey.value !== null && store.has(section, exportKey.value)
      ? t('admin.realmConfig.selection.removeFrom', {}, 'Remove from export selection')
      : t('admin.realmConfig.selection.addTo', {}, 'Add to export selection'))

  function exportMenuToggle() {
    if (exportKey.value !== null) store.toggle(section, exportKey.value)
  }

  return { exportMenuVisible, exportMenuLabel, exportMenuToggle }
}
