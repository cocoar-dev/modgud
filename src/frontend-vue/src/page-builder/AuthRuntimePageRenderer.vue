<script setup lang="ts">
import { computed } from 'vue'
import {
  CoarPageRenderer,
  type ActionHandler,
  type AuthPageLocale,
  type PageConfig,
  type PageNode,
} from '@cocoar/vue-page-builder'
import { CoarThemeScope } from '@cocoar/vue-ui'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useAuthPageCodeRuntime } from './authPageCodeRuntime'
import { createAuthPageTheme } from './authPageTheme'

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
const pageTheme = computed(() => createAuthPageTheme(appConfig.config.PageTheme))
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
  <CoarThemeScope class="auth-runtime-page-theme" :theme="pageTheme" mode="auto">
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
  </CoarThemeScope>
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
