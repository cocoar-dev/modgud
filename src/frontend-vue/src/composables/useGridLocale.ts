import { useI18n } from '@cocoar/vue-localization'

/**
 * Shared German-first localisation for every CoarDataGrid list view.
 *
 * Both CoarDataGrid and the underlying AG-Grid default to English chrome —
 * the search box reads "Search..." and an empty grid shows "No Rows To Show".
 * In a German session those leak through untranslated. This composable routes
 * the two surfaces through i18n so a German session stays German, applied
 * uniformly instead of per-grid ad-hoc.
 *
 * Usage in a list view's `<script setup>`:
 *   const { searchPlaceholder, gridLocaleText } = useGridLocale()
 *   const builder = CoarGridBuilder.create<T>()
 *     .option('localeText', gridLocaleText)
 *     ...
 *   <CoarDataGrid :builder="builder" :search-placeholder="searchPlaceholder" show-search ... />
 *
 * Note: like the rest of the app's grid builders, these strings are resolved
 * once at setup time (the builder is constructed once), matching the existing
 * pattern for column headers etc.
 */
export function useGridLocale() {
  const { t } = useI18n()
  return {
    searchPlaceholder: t('grid.search', {}, 'Suchen…'),
    // AG-Grid `localeText` overrides — only the keys that actually surface in
    // these list grids. Unset keys fall back to AG-Grid's built-in English.
    gridLocaleText: {
      noRowsToShow: t('grid.noRowsToShow', {}, 'Keine Einträge vorhanden'),
    } as Record<string, string>,
  }
}
