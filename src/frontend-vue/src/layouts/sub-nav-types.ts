import type { RouteLocationRaw } from 'vue-router'

/**
 * Ein Eintrag in der Sub-Navigation. Entweder navigiert per `to` (Vue-Router-
 * Link), oder löst per `onClick` eine Action aus. Beides gleichzeitig hat
 * keine Bedeutung — wenn `to` gesetzt ist, gewinnt die Navigation.
 */
export interface SubNavItem {
  label: string
  icon?: string
  to?: RouteLocationRaw
  onClick?: () => void
  /** Optional: aktuelles Active-State, wenn die Library-Komponente das nicht selbst herleiten kann. */
  active?: boolean
  /** Optional: deaktiviert das Item. */
  disabled?: boolean
  /** Optional: Sichtbarkeits-Check (Permission-Gate o.ä.). `false` → Item wird nicht gerendert. */
  visible?: boolean
}

/**
 * Eine Gruppe für `SubNavLayoutGrouped` — mehrere Items unter einer
 * Themen-Überschrift.
 */
export interface SubNavGroup {
  /** Anzeige-Titel der Gruppe. Leer/undefined → kein Heading gerendert. */
  title?: string
  items: SubNavItem[]
}
