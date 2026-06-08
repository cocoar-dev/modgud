export type TileTone = 'rose' | 'amber' | 'emerald' | 'sky' | 'violet' | 'blue'

export interface KpiTile {
  key: string
  icon: string
  tone: TileTone
  /** Display value. `null` means loading; show an em-dash. Empty string
   *  short-circuits the spinner+dash logic for fraction-style values that
   *  format themselves. */
  value: string | null
  /** When true, the value renders in rose (e.g. failure counters > 0). */
  bad?: boolean
  /** When true, the value renders in amber (warn) without going full red. */
  warn?: boolean
  /** Pulled while the underlying data is still loading. */
  loading: boolean
  caption: string
  onClick?: () => void
}
