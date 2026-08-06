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
  const accent = appConfig.config.Branding.PrimaryColor
  if (!accent) return {} as Record<string, string>
  return {
    // Cocoar derives its complete accessible accent palette from this base hue.
    '--coar-accent': accent,
    // Compatibility token for host-registered custom page elements.
    '--coar-color-primary': accent,
  }
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

.auth-runtime-page-renderer {
  --coar-shadow-small: var(--coar-card-shadow);
}
</style>
