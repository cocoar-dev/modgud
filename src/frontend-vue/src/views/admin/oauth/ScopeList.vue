<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
  CoarMenuDivider,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useOAuthScopeStore } from '@/stores/oauthScope.store'
import { useAppContextStore } from '@/stores/appContext.store'
import { useUI } from '@/composables/useUI'
import { useGridLocale } from '@/composables/useGridLocale'
import { useClone, buildClonePrefill, SCOPE_CLONE } from '@/composables/useClone'
import type { OAuthScopeDto } from '@/models/oauth'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const { stage } = useClone()
const store = useOAuthScopeStore()
const appCtx = useAppContextStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.oauthScopes.title', {}, 'OAuth-Scopes')
  ctx.header.icon = 'tags'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() =>
  store.scopes.filter((s) => appCtx.matchesSingleAppId(s.AppId)))
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])
const selectedIsStandard = ref(false)

const showEmpty = computed(() => store.loaded && store.scopes.length === 0)

const builder = applyListGridDefaults(CoarGridBuilder.create<OAuthScopeDto>(), { openable: true })
  .persistColumnState('admin-oauth-scopes')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
  .searchHighlight()
  // Standard OIDC scopes (openid / email / profile / roles /
  // offline_access) ship with the IdP and aren't admin-editable —
  // dim the row to telegraph that.
  .rowClassRules({
    'is-system': (p: any) => p.data?.IsStandard === true,
  })
  .rowSelection('single')
  .onCellDoubleClicked((event) => {
    if (event.data) navigateToModal(event.data.Id)
  })
  .onCellContextMenu((event) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    const selected = event.api.getSelectedRows() as OAuthScopeDto[]
    selectedIds.value = selected.map((r) => r.Id)
    selectedIsStandard.value = selected.some((r) => r.IsStandard)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col
      .wrap(col.field('Name').header('Name', 'admin.oauthScopes.name').flex(1).minWidth(140))
      .left({
        icon: (r: any) => r?.IsStandard ? 'lock' : null,
        color: 'var(--coar-text-neutral-secondary, #9ca3af)',
        tooltip: t('admin.system.lockedHint', {}, 'OIDC standard scope — read-only'),
      }),
    (col) => col.field('DisplayName').header('Display Name', 'admin.oauthScopes.displayName').flex(1),
    (col) => col.field('Description').header('Description', 'admin.oauthScopes.description').flex(2),
    (col) => col.field('Resources').header('Resources', 'admin.oauthScopes.resources').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Resources ?? []).join(', ')),
    (col) => col.tag('Enabled', {
      variantMap: { active: 'success', inactive: 'neutral' },
      i18nPrefix: 'common.statusTag.',
    })
      .header('Enabled', 'common.enabled').width(110)
      .option('valueGetter', (p: any) => p.data?.Enabled ? 'active' : 'inactive'),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (selectedIsStandard.value) {
    alert(t('admin.oauthScopes.cannotDeleteStandard', {}, 'Standard OIDC scopes cannot be deleted.'))
    return
  }
  if (!confirm(t('admin.oauthScopes.confirmDelete', {}, 'Really delete this scope?'))) return
  try { await store.remove(id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

// Clone: load the full scope, blank the immutable Name, open Create pre-filled.
async function cloneSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  try {
    const source = await store.loadOne(id)
    if (!source) return
    stage(SCOPE_CLONE.entity, buildClonePrefill(source, SCOPE_CLONE.descriptor))
    navigateToModal('create')
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
}

onMounted(() => store.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">
          {{ t('common.create', {}, 'Create') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="tags"
      :title="t('admin.oauthScopes.title', {}, 'OAuth-Scopes')"
      :description="t('admin.oauthScopes.emptyHint', {}, 'A scope is a named permission a client can request during login (e.g. read access to an API). Define your own scopes here beyond the standard OIDC ones.')"
      :cta-label="t('common.create', {}, 'Create')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" :icon="selectedIsStandard ? 'eye' : 'pencil'"
        @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus"
        @clicked="navigateToModal('create')" />
      <CoarMenuItem :label="t('common.clone', {}, 'Clone')" icon="copy"
        @clicked="cloneSelected" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2"
        :disabled="selectedIsStandard" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus"
        @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
