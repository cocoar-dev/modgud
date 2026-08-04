<script setup lang="ts">
import { computed } from 'vue'
import {
  CoarPageRenderer,
  type ActionHandler,
  type AuthPageLocale,
  type PageConfig,
  type PageNode,
} from '@cocoar/vue-page-builder'
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
const { pageCodeValues, onRuntimeChange, runPageAction } = useAuthPageCodeRuntime({
  pageId: props.pageId,
  schema,
  context,
})
</script>

<template>
  <CoarPageRenderer
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
</template>
