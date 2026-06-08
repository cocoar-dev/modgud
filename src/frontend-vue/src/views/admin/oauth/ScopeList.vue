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
import type { OAuthScopeDto } from '@/models/oauth'
import GridEmptyState from '@/components/GridEmptyState.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
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
    selectedIds.value = event.api.getSelectedRows().map((r: OAuthScopeDto) => r.Id)
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
    (col) => col.field('Enabled').header('Enabled', 'common.enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled
        ? t('common.yes', {}, 'Ja')
        : t('common.no', {}, 'Nein')),
  ])

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (!confirm(t('common.confirmDelete', {}, 'Wirklich löschen?'))) return
  try { await store.remove(id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

onMounted(() => store.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="builder" :search-placeholder="searchPlaceholder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">
          {{ t('common.create', {}, 'Erstellen') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="tags"
      :title="t('admin.oauthScopes.title', {}, 'OAuth-Scopes')"
      :description="t('admin.oauthScopes.emptyHint', {}, 'A scope is a named permission a client can request during login (e.g. read access to an API). Define your own scopes here beyond the standard OIDC ones.')"
      :cta-label="t('common.create', {}, 'Erstellen')"
      @cta="navigateToModal('create')"
    />

    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Öffnen')" icon="pencil"
        @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Erstellen')" icon="plus"
        @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Löschen')" icon="trash-2" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Erstellen')" icon="plus"
        @clicked="navigateToModal('create')" />
    </CoarContextMenu>
  </div>
</template>
