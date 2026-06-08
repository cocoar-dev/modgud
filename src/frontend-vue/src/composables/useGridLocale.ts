import { useI18n } from '@cocoar/vue-localization'
import { CoarGridBuilder, type ColDef } from '@cocoar/vue-data-grid'

/**
 * Shared chrome for every CoarDataGrid admin list view — German-first
 * localisation plus the column/row defaults that make the lists readable and
 * discoverable (UI/UX wave 2).
 *
 * Both CoarDataGrid and the underlying AG-Grid default to English chrome (the
 * search box reads "Search..." and an empty grid shows "No Rows To Show") and
 * give no cue that rows open or that a clipped cell hides more text. This
 * composable centralises the fixes so every list applies them uniformly
 * instead of per-grid ad-hoc.
 *
 * Usage in a list view's `<script setup>`:
 *   const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
 *   const builder = applyListGridDefaults(CoarGridBuilder.create<T>(), { openable: true })
 *     .persistColumnState('admin-x')
 *     .rowDataRef(rows)
 *     .columns([...])
 *   <CoarDataGrid :builder="builder" :search-placeholder="searchPlaceholder" show-search ... />
 *
 * Note: like the rest of the app's grid builders, these strings are resolved
 * once at setup time (the builder is constructed once), matching the existing
 * pattern for column headers etc.
 */
export function useGridLocale() {
  const { t } = useI18n()

  const searchPlaceholder = t('grid.search', {}, 'Suchen…')

  // AG-Grid `localeText` overrides — only the keys that actually surface in
  // these list grids. Unset keys fall back to AG-Grid's built-in English.
  const gridLocaleText: Record<string, string> = {
    noRowsToShow: t('grid.noRowsToShow', {}, 'Keine Einträge vorhanden'),
  }

  // Shared default column definition applied to every admin list grid.
  //
  // It deliberately carries ONLY `tooltipValueGetter`. We do NOT put `flex` or
  // `minWidth` here: in AG-Grid a column's own `width` is *ignored* once it
  // inherits `flex` from the default (flex wins over width), and an inherited
  // `minWidth` clamps narrow columns up — either would break the deliberate
  // fixed/pinned identifier columns (UserName w150) and the narrow icon
  // columns (w38/w80). Flex *priority* on the identifier column is therefore
  // set per-column in each view, not here.
  //
  // `tooltipValueGetter` surfaces the full cell text on hover so a clipped
  // value is discoverable (the lists truncate load-bearing columns). It
  // mirrors the *rendered* value (`valueFormatted ?? value`) so valueGetter
  // columns (counts, mapped enum labels, joined arrays) tooltip their
  // displayed text — never a raw array/boolean/GUID. Icon columns opt out
  // per-column via `.option('tooltipValueGetter', () => null)` because their
  // value is a lucide icon name ('check'), not human-readable text.
  const sharedDefaultColDef: ColDef = {
    tooltipValueGetter: (p: any) => {
      const v = p.valueFormatted ?? p.value
      if (v === null || v === undefined || v === '') return null
      return String(v)
    },
  }

  /**
   * Apply the shared admin-grid chrome to a builder in one call:
   *  - German no-rows / locale overlay,
   *  - the shared default column definition (truncation tooltips),
   *  - a discoverable row-open affordance (pointer cursor + hover highlight,
   *    via the `admin-grid-row--openable` row class styled globally in
   *    `assets/styles/main.css`) for grids whose rows open a detail.
   *
   * Pass `{ openable: false }` for read-only log grids that have no detail
   * target, so the pointer cue is not shown where a double-click does nothing.
   *
   * Does NOT set getRowId / persistColumnState / rowData / columns / handlers —
   * those stay per-view. Returns the builder for chaining.
   */
  function applyListGridDefaults<T>(
    builder: CoarGridBuilder<T>,
    opts: { openable?: boolean } = {},
  ): CoarGridBuilder<T> {
    // `sharedDefaultColDef` is the same object for every grid; cast through the
    // generic boundary (it carries no `field`, so the per-row type is irrelevant).
    builder.option('localeText', gridLocaleText).defaultColDef(sharedDefaultColDef as Partial<ColDef<T>>)
    if (opts.openable !== false) {
      builder.option('rowClass', 'admin-grid-row--openable')
    }
    return builder
  }

  return { searchPlaceholder, gridLocaleText, sharedDefaultColDef, applyListGridDefaults }
}
