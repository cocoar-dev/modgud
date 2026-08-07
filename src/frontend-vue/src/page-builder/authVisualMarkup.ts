import type { PageVisualMarkupConfig } from '@cocoar/vue-page-builder'
import type { PageThemeConfig } from '@/stores/appconfig.store'
import instrumentSansDataUrl from '@fontsource-variable/instrument-sans/files/instrument-sans-latin-wght-normal.woff2?inline'
import frauncesItalicDataUrl from '@fontsource-variable/fraunces/files/fraunces-latin-full-italic.woff2?inline'

/**
 * Host-owned capabilities for decorative auth visuals. The font binaries are
 * bundled data URLs, so the opaque iframe never needs network access.
 */
export function createAuthVisualMarkupConfig(theme: PageThemeConfig | null): PageVisualMarkupConfig {
  return {
    themeVariables: {
      '--coar-accent': theme?.AccentColor ?? '#2563eb',
      '--visual-error': theme?.ErrorColor ?? '#e5484d',
      '--visual-surface': '#ffffff',
      '--visual-text': '#16202e',
      '--visual-text-secondary': '#54606e',
      '--visual-line': '#e5e8ec',
    },
    fonts: [
      {
        id: 'instrument-sans-variable',
        family: 'Instrument Sans Variable',
        source: instrumentSansDataUrl,
        format: 'woff2',
        weight: '100 900',
        style: 'normal',
        display: 'swap',
      },
      {
        id: 'fraunces-variable-italic',
        family: 'Fraunces Variable',
        source: frauncesItalicDataUrl,
        format: 'woff2',
        weight: '100 900',
        style: 'italic',
        display: 'swap',
      },
    ],
  }
}
