import type { Component } from 'vue'

/** Width of a widget on the 12-column dashboard grid. */
export type WidgetSize = 'xs' | 's' | 'm' | 'l' | 'xl' | 'full'

/** One widget on the dashboard. List order is display order. */
export interface WidgetPlacement {
  Widget: string
  Size: WidgetSize
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
    })
  }
  return result
}
