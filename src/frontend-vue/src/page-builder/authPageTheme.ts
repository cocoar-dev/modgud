import type { CoarTheme } from '@cocoar/vue-ui'
import type { PageThemeConfig } from '@/stores/appconfig.store'

/** Maps Modgud's allowlisted Application settings to Cocoar's generic scope. */
export function createAuthPageTheme(theme: PageThemeConfig | null): CoarTheme {
  if (!theme) return {}
  return {
    accent: theme.AccentColor ?? undefined,
    error: theme.ErrorColor ?? undefined,
    buttonRadius: theme.ButtonRadiusPx ?? undefined,
    inputRadius: theme.InputRadiusPx ?? undefined,
    cardRadius: theme.CardRadiusPx ?? undefined,
    bodyFontFamily: theme.BodyFontFamily ?? undefined,
    titleFontFamily: theme.TitleFontFamily ?? undefined,
  }
}
