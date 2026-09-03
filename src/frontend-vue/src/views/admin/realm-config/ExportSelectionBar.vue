<script setup lang="ts">
/**
 * The export-selection bar — footer-positioned by MainLayout (right above the
 * draft staging bar) whenever the selection is non-empty. The admin collects
 * entities from the normal admin grids via their context menus ("Add to
 * export selection"), using the grids' full search/filter power; this bar
 * carries the count and the verbs: inspect (popover listing every collected
 * entry with per-item remove), export (opens the selective-export review
 * modal pre-filled with the collection) and clear.
 */
import { computed } from 'vue'
import { CoarButton, CoarIcon, CoarPopover, CoarTag, useToast } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useHttpClient } from '@/composables/useHttpClient'
import { useModalOverlay } from '@/composables/useModalOverlay'
import { MODAL_LG } from '@/router/modal-sizes'
import { useExportSelectionStore } from '@/stores/exportSelection.store'
import { draftErrorMessage, type DraftManifest } from '@/stores/realmDraft.store'
import SelectiveExportModal from './SelectiveExportModal.vue'

const { t } = useI18n()
const toast = useToast()
const store = useExportSelectionStore()
const modal = useModalOverlay()
const configHttp = useHttpClient('/api/admin/realm-config')

const SECTION_ICONS: Record<string, string> = {
  apps: 'layout-grid', apis: 'server', scopes: 'tags', clients: 'app-window',
  roles: 'shield', groups: 'users-round', users: 'users',
  loginProviders: 'log-in', positions: 'briefcase',
}

function sectionLabel(name: string): string {
  return t(`admin.realmConfig.section.${name}`, {}, {
    apps: 'Applications', apis: 'OAuth APIs', scopes: 'OAuth scopes',
    clients: 'OAuth clients', loginProviders: 'Login providers',
    roles: 'Roles', users: 'Users', groups: 'Groups', positions: 'Positions',
  }[name] ?? name)
}

/** Grouped by section, in a stable display order. */
const grouped = computed(() => {
  const order = ['apps', 'apis', 'scopes', 'clients', 'roles', 'groups', 'users', 'loginProviders', 'positions']
  return order
    .map((section) => ({
      section,
      keys: store.items.filter((i) => i.section === section).map((i) => i.key),
    }))
    .filter((g) => g.keys.length > 0)
})

async function openExport() {
  try {
    const exported = await configHttp.addPath('export').get<DraftManifest>()
    await modal.open(SelectiveExportModal, MODAL_LG, {
      manifest: exported,
      preselected: [...store.selectionKeys],
    })
  } catch (e) {
    toast.error(draftErrorMessage(e))
  }
}
</script>

<template>
  <div v-if="store.count > 0" class="selection-bar">
    <CoarPopover mode="click" :offset="8">
      <button type="button" class="bar-trigger"
        :aria-label="t('admin.realmConfig.selection.show', {}, 'Show collected entries')">
        <CoarIcon name="list-checks" size="s" class="bar-icon" />
        <span class="bar-name">{{ t('admin.realmConfig.selection.title', {}, 'Export selection') }}</span>
        <CoarTag variant="info" size="s">
          {{ t('admin.realmConfig.selection.count', { count: store.count }, `${store.count} collected`) }}
        </CoarTag>
        <CoarIcon name="chevron-down" size="s" class="bar-chevron" />
      </button>
      <template #content>
        <div class="selection-panel">
          <div v-for="group in grouped" :key="group.section" class="panel-section">
            <div class="panel-section-head">
              <CoarIcon :name="SECTION_ICONS[group.section] ?? 'file-json'" size="s" />
              <span>{{ sectionLabel(group.section) }}</span>
            </div>
            <div v-for="key in group.keys" :key="key" class="panel-entry">
              <span class="panel-key">{{ key }}</span>
              <button type="button" class="panel-remove"
                :aria-label="t('common.remove', {}, 'Remove')"
                @click="store.remove(group.section, key)">
                <CoarIcon name="x" size="s" />
              </button>
            </div>
          </div>
        </div>
      </template>
    </CoarPopover>

    <span class="bar-spacer" />

    <CoarButton size="s" variant="ghost" @click="store.clear()">
      {{ t('admin.realmConfig.selection.clear', {}, 'Clear') }}
    </CoarButton>
    <CoarButton size="s" variant="primary" @click="openExport">
      {{ t('admin.realmConfig.selection.export', {}, 'Export…') }}
    </CoarButton>
  </div>
</template>

<style scoped>
.selection-bar {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.4rem 1rem;
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
  background: var(--coar-background-neutral-secondary, #f7f8fa);
  flex-shrink: 0;
}

.bar-trigger {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  border: none;
  background: none;
  padding: 2px 4px;
  cursor: pointer;
  border-radius: 6px;
}
.bar-trigger:hover {
  background: var(--coar-background-neutral-tertiary, #eceef1);
}

.bar-icon,
.bar-chevron {
  color: var(--coar-text-neutral-secondary, #6b7280);
}

.bar-name {
  font-weight: 600;
  font-size: 0.8rem;
  white-space: nowrap;
}

.bar-spacer {
  flex: 1;
}

.selection-panel {
  max-height: 320px;
  min-width: 260px;
  overflow-y: auto;
  padding: 6px 4px;
}

.panel-section-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
  font-size: 0.75rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  padding: 6px 8px 2px;
}

.panel-entry {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 2px 8px 2px 28px;
}

.panel-key {
  font-family: var(--coar-font-mono, monospace);
  font-size: 12.5px;
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-remove {
  border: none;
  background: none;
  padding: 2px;
  cursor: pointer;
  color: var(--coar-text-neutral-secondary, #6b7280);
  border-radius: 4px;
  display: flex;
}
.panel-remove:hover {
  color: var(--coar-text-semantic-error, #dc2626);
  background: var(--coar-background-neutral-tertiary, #eceef1);
}
</style>
