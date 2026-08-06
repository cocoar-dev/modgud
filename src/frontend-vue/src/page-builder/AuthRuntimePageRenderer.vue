<script setup lang="ts">
import { computed } from 'vue'
import {
  CoarPageRenderer,
  type ActionHandler,
  type AuthPageLocale,
  type PageConfig,
  type PageNode,
} from '@cocoar/vue-page-builder'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useAuthPageCodeRuntime } from './authPageCodeRuntime'

const props = defineProps<{
  pageId: string
  schema: PageNode
  config: PageConfig
  actions: Record<string, ActionHandler>
  fallbackSchema: PageNode
  runtimeContext: Record<string, unknown>
  viewState: string
  locale: AuthPageLocale
}>()

// Keep the worker/session lifecycle inside the renderer branch. Fixed auth
// fallbacks never mount this component, so they neither start a worker nor
// push reactive state into a session that has not been initialized yet.
const schema = computed(() => props.schema)
const context = computed(() => props.runtimeContext)
const appConfig = useAppConfigStore()
const pageThemeStyle = computed<Record<string, string>>(() => {
  const theme = appConfig.config.PageTheme
  if (!theme) return {} as Record<string, string>

  const style: Record<string, string> = {}
  if (theme.AccentColor) {
    // Cocoar derives its complete accessible accent palette from this base hue.
    style['--coar-accent'] = theme.AccentColor
    style['--coar-color-primary'] = theme.AccentColor
  }
  if (theme.ErrorColor) style['--coar-error'] = theme.ErrorColor
  if (theme.ButtonRadiusPx !== null) style['--coar-button-radius'] = `${theme.ButtonRadiusPx}px`
  if (theme.InputRadiusPx !== null) style['--coar-input-radius'] = `${theme.InputRadiusPx}px`
  if (theme.CardRadiusPx !== null) style['--coar-card-radius'] = `${theme.CardRadiusPx}px`
  return style
})
const { pageCodeValues, onRuntimeChange, runPageAction } = useAuthPageCodeRuntime({
  pageId: props.pageId,
  schema,
  context,
})
</script>

<template>
  <!--
    Application theme tokens are scoped to this branch. This component only
    mounts for a selected custom page, and the effective Branding + schema came
    from the same server-resolved Host/OAuth-client app-info response.
  -->
  <div class="auth-runtime-page-theme" :style="pageThemeStyle">
    <CoarPageRenderer
      class="auth-runtime-page-renderer"
      :schema="schema"
      :config="config"
      :actions="actions"
      :fallback-schema="fallbackSchema"
      :runtime-context="runtimeContext"
      :view-state="viewState"
      :locale="locale"
      :page-code-values="pageCodeValues"
      :on-action="runPageAction"
      @runtime-change="onRuntimeChange"
    />
  </div>
</template>

<style scoped>
/*
 * Auth page documents use min-height: 100%, while the generic renderer is
 * intentionally content-sized. Give this full-page consumer a definite
 * containing block so the document fills the viewport and can still grow
 * beyond it when a small screen needs scrolling.
 */
.auth-runtime-page-theme,
.auth-runtime-page-renderer {
  height: 100vh;
  height: 100dvh;
}

/*
 * Cocoar's derived palettes are computed on :root. Re-declare them at the
 * custom-page boundary so a locally overridden base hue is actually used by
 * semantic component tokens, without changing the document/global theme.
 */
.auth-runtime-page-theme {
  --coar-color-accent-50: oklch(from var(--coar-accent) 0.97 0.012 h);
  --coar-color-accent-100: oklch(from var(--coar-accent) 0.92 0.035 h);
  --coar-color-accent-200: oklch(from var(--coar-accent) 0.84 0.075 h);
  --coar-color-accent-300: oklch(from var(--coar-accent) 0.74 0.115 h);
  --coar-color-accent-400: oklch(from var(--coar-accent) 0.66 0.145 h);
  --coar-color-accent-500: var(--coar-accent);
  --coar-color-accent-600: oklch(from var(--coar-accent) 0.53 0.15 h);
  --coar-color-accent-700: oklch(from var(--coar-accent) 0.47 0.14 h);
  --coar-color-accent-800: oklch(from var(--coar-accent) 0.39 0.12 h);
  --coar-color-accent-900: oklch(from var(--coar-accent) 0.31 0.095 h);
  --coar-color-red-50: oklch(from var(--coar-error) 0.97 0.012 h);
  --coar-color-red-100: oklch(from var(--coar-error) 0.92 0.035 h);
  --coar-color-red-200: oklch(from var(--coar-error) 0.84 0.07 h);
  --coar-color-red-300: oklch(from var(--coar-error) 0.74 0.11 h);
  --coar-color-red-400: oklch(from var(--coar-error) 0.66 0.145 h);
  --coar-color-red-500: var(--coar-error);
  --coar-color-red-600: oklch(from var(--coar-error) 0.47 0.13 h);
  --coar-color-red-700: oklch(from var(--coar-error) 0.40 0.11 h);
  --coar-color-red-800: oklch(from var(--coar-error) 0.33 0.09 h);
  --coar-color-red-900: oklch(from var(--coar-error) 0.26 0.07 h);
  --coar-background-accent-primary: var(--coar-color-accent-500);
  --coar-background-accent-secondary: var(--coar-color-accent-100);
  --coar-background-accent-tertiary: var(--coar-color-accent-50);
  --coar-background-accent-hover: var(--coar-color-accent-600);
  --coar-background-accent-active: var(--coar-color-accent-700);
  --coar-background-accent-tertiary-active: var(--coar-color-accent-200);
  --coar-text-accent-primary: var(--coar-color-accent-600);
  --coar-text-accent-secondary: var(--coar-color-accent-500);
  --coar-border-accent-primary: var(--coar-color-accent-500);
  --coar-border-accent-secondary: var(--coar-color-accent-300);
  --coar-surface-accent-secondary: var(--coar-color-accent-100);
  --coar-icon-accent-primary: var(--coar-color-accent-700);
  --coar-background-semantic-error-bold: var(--coar-color-red-600);
  --coar-background-semantic-error-hover: var(--coar-color-red-700);
  --coar-background-semantic-error-active: var(--coar-color-red-800);
  --coar-background-semantic-error-subtlest: var(--coar-color-red-50);
  --coar-background-semantic-error-subtle: var(--coar-color-red-100);
  --coar-text-semantic-error-bold: var(--coar-color-red-800);
  --coar-text-semantic-error-subtle: var(--coar-color-red-700);
  --coar-border-semantic-error: var(--coar-color-red-600);
  --coar-border-semantic-error-bold: var(--coar-color-red-800);
  --coar-border-semantic-error-subtle: var(--coar-color-red-200);
  --coar-icon-semantic-error-bold: var(--coar-color-red-800);
  --coar-icon-semantic-error-subtle: var(--coar-color-red-700);
  --coar-focus-color: var(--coar-color-accent-300);
  --coar-progress-bar-fill-color: var(--coar-background-accent-primary);
  --coar-spinner-color: var(--coar-background-accent-primary);
  --coar-breadcrumb-link-color: var(--coar-text-accent-primary);
  --coar-pagination-active-background: var(--coar-background-accent-primary);
  --coar-button-danger-bg: var(--coar-background-semantic-error-bold);
  --coar-button-danger-bg-hover: var(--coar-background-semantic-error-hover);
  --coar-button-danger-bg-active: var(--coar-background-semantic-error-active);
  /* PageBuilder alpha.10 still consumes these legacy semantic aliases. */
  --coar-surface-accent-subtle: var(--coar-surface-accent-secondary);
  --coar-text-accent: var(--coar-text-accent-primary);
  --coar-border-accent: var(--coar-border-accent-primary);
  --coar-surface-semantic-error-subtle: var(--coar-background-semantic-error-subtle);
}

:global([data-theme='dark']) .auth-runtime-page-theme {
  --coar-color-accent-50: oklch(from var(--coar-accent) 0.20 0.045 h);
  --coar-color-accent-100: oklch(from var(--coar-accent) 0.25 0.065 h);
  --coar-color-accent-200: oklch(from var(--coar-accent) 0.32 0.095 h);
  --coar-color-accent-300: oklch(from var(--coar-accent) 0.42 0.13 h);
  --coar-color-accent-400: oklch(from var(--coar-accent) 0.52 0.155 h);
  --coar-color-accent-500: var(--coar-accent);
  --coar-color-accent-600: oklch(from var(--coar-accent) 0.68 0.14 h);
  --coar-color-accent-700: oklch(from var(--coar-accent) 0.76 0.11 h);
  --coar-color-accent-800: oklch(from var(--coar-accent) 0.85 0.07 h);
  --coar-color-accent-900: oklch(from var(--coar-accent) 0.93 0.035 h);
  --coar-color-red-50: oklch(from var(--coar-error) 0.20 0.04 h);
  --coar-color-red-100: oklch(from var(--coar-error) 0.25 0.06 h);
  --coar-color-red-200: oklch(from var(--coar-error) 0.32 0.09 h);
  --coar-color-red-300: oklch(from var(--coar-error) 0.42 0.12 h);
  --coar-color-red-400: oklch(from var(--coar-error) 0.52 0.145 h);
  --coar-color-red-500: var(--coar-error);
  --coar-color-red-600: oklch(from var(--coar-error) 0.68 0.12 h);
  --coar-color-red-700: oklch(from var(--coar-error) 0.76 0.09 h);
  --coar-color-red-800: oklch(from var(--coar-error) 0.85 0.06 h);
  --coar-color-red-900: oklch(from var(--coar-error) 0.93 0.03 h);
  --coar-background-accent-secondary: var(--coar-color-accent-200);
  --coar-background-accent-tertiary: var(--coar-color-accent-100);
  --coar-background-accent-hover: var(--coar-color-accent-400);
  --coar-background-accent-active: var(--coar-color-accent-300);
  --coar-background-accent-tertiary-active: var(--coar-color-accent-300);
  --coar-surface-accent-secondary: var(--coar-color-accent-200);
  --coar-background-semantic-error-bold: var(--coar-color-red-500);
  --coar-background-semantic-error-hover: var(--coar-color-red-400);
  --coar-background-semantic-error-active: var(--coar-color-red-300);
  --coar-background-semantic-error-subtlest: var(--coar-color-red-100);
  --coar-background-semantic-error-subtle: var(--coar-color-red-200);
  --coar-text-semantic-error-bold: var(--coar-color-red-900);
  --coar-border-semantic-error: var(--coar-color-red-500);
  --coar-border-semantic-error-bold: var(--coar-color-red-600);
  --coar-border-semantic-error-subtle: var(--coar-color-red-300);
  --coar-icon-semantic-error-bold: var(--coar-color-red-900);
}

.auth-runtime-page-renderer {
  --coar-shadow-small: var(--coar-card-shadow);
}
</style>
