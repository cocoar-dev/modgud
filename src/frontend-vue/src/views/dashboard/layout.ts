import type { Component } from 'vue'

/** Width of a widget on the 12-column dashboard grid. */
export type WidgetSize = 'xs' | 's' | 'm' | 'l' | 'xl' | 'full'

/** A widget's own settings: option key → chosen values. */
export type WidgetOptions = Record<string, string[]>

/** One widget on the dashboard. List order is display order. */
export interface WidgetPlacement {
  Widget: string
  Size: WidgetSize
  /** Only what differs from nothing-chosen-yet; absent = the widget's defaults. */
  Options?: WidgetOptions | null
}

export interface WidgetOptionChoice {
  value: string
  labelKey: string
  labelEn: string
  /** Offered only to viewers holding ANY of these (the data is gated server-side anyway). */
  requirePermissions?: string[]
  /** Offered only while this deployment feature is on. */
  requireFeature?: 'PageBuilder' | 'PositionTerminals'
}

/** One setting of a widget: pick several (`multi`) or exactly one (`single`). */
export interface WidgetOptionDefinition {
  key: string
  labelKey: string
  labelEn: string
  kind: 'multi' | 'single'
  choices: WidgetOptionChoice[]
  defaults: string[]
}

export interface WidgetDefinition {
  id: string
  /** i18n key + English fallback for the catalog and the edit chrome. */
  titleKey: string
  titleEn: string
  icon: string
  /**
   * The viewer needs ANY of these to see the widget. Empty = everyone. This
   * only decides visibility — the data itself is gated by the endpoint.
   */
  requirePermissions: string[]
  /** Widths the widget lays out well at, narrowest first. */
  sizes: WidgetSize[]
  defaultSize: WidgetSize
  component: Component
  props?: Record<string, unknown>
  /** Settings the user can change; the component receives them as its `options` prop. */
  options?: WidgetOptionDefinition[]
}

/**
 * The complete option values a widget renders with: what the placement stores,
 * reduced to choices the widget knows, with defaults filling the rest.
 */
export function resolveOptions(def: WidgetDefinition, stored?: WidgetOptions | null): WidgetOptions {
  const result: WidgetOptions = {}
  for (const option of def.options ?? []) {
    const known = new Set(option.choices.map(c => c.value))
    const chosen = stored?.[option.key]?.filter(v => known.has(v))
    if (chosen === undefined) result[option.key] = [...option.defaults]
    else if (option.kind === 'single') result[option.key] = chosen.length ? [chosen[0]!] : [...option.defaults]
    // An empty multi-selection is a choice, not a missing one.
    else result[option.key] = option.choices.map(c => c.value).filter(v => chosen.includes(v))
  }
  return result
}

/**
 * Turns a stored layout into what this viewer actually gets: placements whose
 * widget still exists and which they may see, each at a size the widget supports.
 * A layout can therefore never show more than the catalog and permissions allow.
 */
export function resolveLayout(
  stored: WidgetPlacement[],
  catalog: Map<string, WidgetDefinition>,
  canSee: (def: WidgetDefinition) => boolean,
): WidgetPlacement[] {
  const seen = new Set<string>()
  const result: WidgetPlacement[] = []
  for (const placement of stored) {
    const def = catalog.get(placement.Widget)
    if (!def || seen.has(def.id) || !canSee(def)) continue
    seen.add(def.id)
    result.push({
      Widget: def.id,
      Size: def.sizes.includes(placement.Size) ? placement.Size : def.defaultSize,
      ...(def.options && placement.Options ? { Options: placement.Options } : {}),
    })
  }
  return result
}
