<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  CoarPageBuilder,
  CURRENT_PAGE_SCHEMA_VERSION,
  normalizePageSchema,
  type ElementNode,
  type PageCompositionDefinition,
  type PageNode,
  type PageRootNode,
} from '@cocoar/vue-page-builder'
import { CoarButton, CoarFormField, CoarNotice, CoarTextInput } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useUI } from '@/composables/useUI'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { usePageCompositionsApi } from '@/composables/usePageCompositionsApi'
import {
  AUTH_PAGE_SLOTS,
  authPageLocale,
  createAuthPageConfig,
  type AuthPageSlot,
} from '@/page-builder/authPageConfig'
import { createAuthRuntimeContext } from '@/page-builder/authPageContext'
import { authRuntimeHost } from '@/page-builder/authPageCodeRuntime'
import { createAuthPageTheme } from '@/page-builder/authPageTheme'

const { t, language } = useI18n()
const ui = useUI()
const route = useRoute()
const router = useRouter()
const appConfig = useAppConfigStore()
const { repository } = usePageCompositionsApi()

const compositionId = computed(() => String(route.params.compositionId ?? 'new'))
const isNew = computed(() => compositionId.value === 'new')
const requestedVersion = computed(() => typeof route.query.version === 'string'
  ? route.query.version
  : undefined)
const previewSlot = ref<AuthPageSlot>('login')
const name = ref('')
const baseVersion = ref<string | null>(null)
const versions = ref<string[]>([])
const selectedVersion = ref<string | null>(null)
const loading = ref(true)
const saving = ref(false)
const error = ref<string | null>(null)
const saved = ref(false)

function emptyRoot(): ElementNode {
  return {
    id: crypto.randomUUID(),
    type: 'stack',
    name: 'compositionRoot',
    props: { direction: 'column' },
    children: [],
  }
}

function workspace(root: ElementNode = emptyRoot()): PageRootNode {
  return {
    id: 'composition-editor-workspace',
    type: 'page',
    schemaVersion: CURRENT_PAGE_SCHEMA_VERSION,
    children: [root],
  }
}

const schema = ref<PageNode>(workspace())
const pageConfig = computed(() => createAuthPageConfig(
  previewSlot.value,
  authPageLocale(language.value),
  undefined,
  appConfig.config.PageTheme,
))
const previewState = computed(() => ({
  login: 'credentials',
  'password-forgot': 'form',
  logout: 'complete',
  consent: 'prompt',
})[previewSlot.value])
const previewContext = computed(() => createAuthRuntimeContext({
  config: appConfig.config,
  viewState: previewState.value,
}))
const previewTheme = computed(() => createAuthPageTheme(appConfig.config.PageTheme))
const previewRuntimePageId = computed(() => `modgud-composition-editor:${compositionId.value}:${baseVersion.value ?? 'new'}`)

watch([language, compositionId], () => ui.set(ctx => {
  ctx.header.title = t('nav.platform', {}, 'Platform')
  ctx.header.subTitle = `${t('admin.customization.compositions.title', {}, 'Compositions')} · ${name.value || (isNew.value ? t('common.new', {}, 'New') : compositionId.value)}`
  ctx.header.icon = 'copy'
  ctx.content.container = false
}), { immediate: true })

function editableRoot(): ElementNode {
  if (schema.value.type !== 'page' || !schema.value.children || schema.value.children.length !== 1)
    throw new Error('A composition must contain exactly one root element.')
  const root = schema.value.children[0]!
  if (root.type === 'page') throw new Error('A composition root cannot be a Page.')
  return root as ElementNode
}

function loadDefinition(definition: PageCompositionDefinition) {
  name.value = definition.name
  baseVersion.value = definition.version
  selectedVersion.value = definition.version
  schema.value = normalizePageSchema(workspace(definition.root), { elements: pageConfig.value.elements }).schema
}

async function load(version?: string) {
  loading.value = true
  error.value = null
  try {
    if (isNew.value) {
      name.value = ''
      baseVersion.value = null
      versions.value = []
      selectedVersion.value = null
      schema.value = workspace()
      return
    }
    const summaries = await repository.list()
    const summary = summaries.find(item => item.id === compositionId.value)
    if (!summary) throw new Error('Composition not found.')
    versions.value = [...(summary.versions ?? [summary.latestVersion])]
    const definition = await repository.get(compositionId.value, version)
    if (!definition) throw new Error(`Composition ${compositionId.value}${version ? `@${version}` : ''} not found.`)
    loadDefinition(definition)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    loading.value = false
  }
}

async function save() {
  if (!name.value.trim()) {
    error.value = t('admin.customization.compositions.nameRequired', {}, 'Give this composition a name first.')
    return
  }
  saving.value = true
  error.value = null
  try {
    const root = editableRoot()
    if (isNew.value) {
      const created = await repository.create({ name: name.value.trim(), root })
      loadDefinition(created)
      await router.replace(`/platform/customization/compositions/${encodeURIComponent(created.id)}`)
    } else {
      if (!baseVersion.value) throw new Error('Missing base version.')
      const published = await repository.publish({ id: compositionId.value, baseVersion: baseVersion.value, root })
      loadDefinition(published)
      const summaries = await repository.list()
      versions.value = [...(summaries.find(item => item.id === compositionId.value)?.versions ?? [published.version])]
    }
    saved.value = true
    setTimeout(() => { saved.value = false }, 1500)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause)
  } finally {
    saving.value = false
  }
}

async function loadSelectedVersion() {
  if (selectedVersion.value) await load(selectedVersion.value)
}

watch([compositionId, requestedVersion], () => load(requestedVersion.value), { immediate: true })
</script>

<template>
  <div class="editor-page">
    <div class="editor-toolbar">
      <CoarButton size="s" variant="ghost" icon-start="arrow-left" @click="router.push('/platform/customization/compositions')">
        {{ t('common.back', {}, 'Back') }}
      </CoarButton>
      <CoarFormField class="name-field">
        <CoarTextInput v-model="name" size="s" :disabled="!isNew" placeholder="Composition name" />
      </CoarFormField>
      <label class="slot-select">
        Preview as
        <select v-model="previewSlot">
          <option v-for="slot in AUTH_PAGE_SLOTS" :key="slot" :value="slot">{{ slot }}</option>
        </select>
      </label>
      <div class="toolbar-spacer" />
      <span v-if="baseVersion" class="version-label">Base v{{ baseVersion }}</span>
      <select v-if="versions.length" v-model="selectedVersion" class="version-select">
        <option v-for="version in versions" :key="version" :value="version">v{{ version }}</option>
      </select>
      <CoarButton v-if="versions.length" size="s" variant="ghost" :disabled="selectedVersion === baseVersion" @click="loadSelectedVersion">
        Load version
      </CoarButton>
      <CoarButton size="s" :loading="saving" @click="save">
        {{ isNew ? t('common.create', {}, 'Create') : t('admin.customization.compositions.publish', {}, 'Publish new version') }}
      </CoarButton>
    </div>

    <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>
    <CoarNotice v-if="saved" truncate variant="success">{{ t('admin.realmSettings.saved', {}, 'Saved.') }}</CoarNotice>
    <CoarNotice variant="info">
      This workspace persists exactly one non-Page root. Published versions are immutable; existing page instances remain pinned until explicitly updated.
    </CoarNotice>

    <p v-if="loading">{{ t('common.loading', {}, 'Loading…') }}</p>
    <CoarPageBuilder
      v-else
      v-model="schema"
      :config="pageConfig"
      :composition-repository="repository"
      composition-management="consume"
      authoring-mode="code"
      :preview-context="previewContext"
      :preview-state="previewState"
      :preview-locale="authPageLocale(language)"
      :preview-theme="previewTheme"
      preview-theme-mode="auto"
      :preview-runtime-host="authRuntimeHost"
      :preview-runtime-page-id="previewRuntimePageId"
      class="builder"
    />
  </div>
</template>

<style scoped>
.editor-page { display: flex; flex-direction: column; gap: .5rem; padding: .5rem 1rem 1rem; min-height: 0; flex: 1; }
.editor-toolbar { display: flex; align-items: center; gap: .5rem; }
.name-field { margin: 0; min-width: 220px; }
.toolbar-spacer { flex: 1; }
.slot-select { display: flex; align-items: center; gap: .35rem; color: var(--coar-text-neutral-secondary); font-size: .75rem; }
.slot-select select, .version-select { border: 1px solid var(--coar-border-neutral-secondary); border-radius: .35rem; padding: .3rem; background: var(--coar-background-neutral-primary); }
.version-label { color: var(--coar-text-neutral-secondary); font-size: .75rem; white-space: nowrap; }
.builder { flex: 1; min-height: 0; --coar-shadow-small: var(--coar-card-shadow); }
</style>
