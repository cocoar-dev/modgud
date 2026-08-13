import type { PageVisualMarkupConfig } from '@cocoar/vue-page-builder'
import type { PageThemeConfig } from '@/stores/appconfig.store'
import instrumentSansDataUrl from '@fontsource-variable/instrument-sans/files/instrument-sans-latin-wght-normal.woff2?inline'
import frauncesItalicDataUrl from '@fontsource-variable/fraunces/files/fraunces-latin-full-italic.woff2?inline'

/**
 * Host-owned capabilities for decorative auth visuals.
 *
 * `visual-markup` renders inside a sealed iframe with no JavaScript, network or
 * DOM access, so the interesting boundary is not what the markup may *do* but
 * what it may *refer to*. These custom properties are that boundary: a tenant's
 * CSS can use them, and nothing else crosses from the application.
 *
 * The names are deliberately generic. An earlier iteration shipped a
 * `stylePreset` with one tenant's panel background baked into the application
 * stylesheet, which only that tenant could use and which 3.0 removed anyway.
 * A vocabulary works for every realm: whoever authors the panel writes ordinary
 * CSS against these names, and the values follow the realm's own theme.
 *
 * Values come from the resolved application theme where it defines one, and
 * otherwise from neutral defaults that read well on a light auth surface.
 */
export function createAuthVisualMarkupConfig(theme: PageThemeConfig | null): PageVisualMarkupConfig {
  const accent = theme?.AccentColor ?? '#2563eb'

  return {
    themeVariables: {
      // Brand — the realm's accent, plus a deepened variant for text set on a
      // light surface, where the plain accent is often too light to read.
      '--brand': accent,
      '--brand-deep': `color-mix(in srgb, ${accent} 82%, #04211a)`,
      '--coar-accent': accent,
      '--error': theme?.ErrorColor ?? '#e5484d',
      '--visual-error': theme?.ErrorColor ?? '#e5484d',

      // Surfaces and rules.
      '--surface': '#ffffff',
      '--surface-sunken': '#f5f7f9',
      '--line': '#e5e8ec',
      '--line-strong': '#c9d0d9',
      '--hover': 'rgba(16, 24, 40, 0.06)',

      // Text, from primary down to the faded state a struck-through item uses.
      '--ink': '#16202e',
      '--ink-soft': '#54606e',
      '--ink-faint': '#6d7885',

      // Retained under their previous names so panels authored against the
      // earlier six-variable vocabulary keep rendering.
      '--visual-surface': '#ffffff',
      '--visual-text': '#16202e',
      '--visual-text-secondary': '#54606e',
      '--visual-line': '#e5e8ec',

      // Shape and motion. The radii follow the theme so a panel matches the
      // form next to it.
      '--radius-s': `${theme?.InputRadiusPx ?? 10}px`,
      '--radius-m': `${theme?.ButtonRadiusPx ?? 12}px`,
      '--radius-l': `${theme?.CardRadiusPx ?? 20}px`,
      '--shadow-pop': '0 8px 18px rgba(16, 24, 40, .08), 0 32px 72px -28px rgba(16, 24, 40, .34)',
      '--ease-out': 'cubic-bezier(.22, 1, .36, 1)',

      // Typography. The bundled families are always available; a theme font
      // takes precedence and falls back to them.
      '--font-ui': theme?.BodyFontFamily
        ? `${theme.BodyFontFamily}, "Instrument Sans Variable", "Segoe UI", sans-serif`
        : '"Instrument Sans Variable", "Segoe UI", sans-serif',
      '--font-display': theme?.TitleFontFamily
        ? `${theme.TitleFontFamily}, "Fraunces Variable", Georgia, serif`
        : '"Fraunces Variable", Georgia, serif',
    },
    fonts: [
      {
        id: 'instrument-sans-variable',
        family: 'Instrument Sans Variable',
        src: instrumentSansDataUrl,
        format: 'woff2',
        weight: '100 900',
        style: 'normal',
        display: 'swap',
      },
      {
        id: 'fraunces-variable-italic',
        family: 'Fraunces Variable',
        src: frauncesItalicDataUrl,
        format: 'woff2',
        weight: '100 900',
        style: 'italic',
        display: 'swap',
      },
    ],
  }
}
