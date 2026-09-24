<script setup lang="ts">
/**
 * The export selection as a header chip — rendered by MainLayout next to the
 * inbox bell whenever the selection is non-empty (admin area only). The admin
 * collects entities from the normal admin grids via their context menus ("Add
 * to export selection"), using the grids' full search/filter power; the chip
 * carries the count, its popover lists every collected entry (per-item
 * remove) plus the verbs: clear, and download (opens the selective-export
 * review modal pre-filled with the collection).
 *
 * A header chip rather than a second footer bar: the footer belongs to the
 * draft staging bar alone, and collecting for an export is not staging.
 */
import { computed, ref } from 'vue'
import { CoarButton, CoarIcon, CoarPopover, useToast } from '@cocoar/vue-ui'
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

// CoarPopover exposes no close() — re-keying it remounts it closed, so the
// popover does not linger above the export modal it just opened.
const popoverKey = ref(0)

const SECTION_ICONS: Record<string, string> = {
  apps: 'layout-grid', apis: 'server', scopes: 'tags', clients: 'app-window',
  roles: 'shield', groups: 'users-round', users: 'users',
  serviceAccounts: 'bot',
  loginProviders: 'log-in', positions: 'briefcase',
}

function sectionLabel(name: string): string {
  return t(`admin.realmConfig.section.${name}`, {}, {
    apps: 'Applications', apis: 'OAuth APIs', scopes: 'OAuth scopes',
    clients: 'OAuth clients', loginProviders: 'Login providers',
    roles: 'Roles', users: 'Users', groups: 'Groups', positions: 'Positions',
    serviceAccounts: 'Service accounts',
  }[name] ?? name)
}

/** Grouped by section, in a stable display order. */
const grouped = computed(() => {
  const order = ['apps', 'apis', 'scopes', 'clients', 'roles', 'groups', 'users', 'serviceAccounts', 'loginProviders', 'positions']
  return order
    .map((section) => ({
      section,
      keys: store.items.filter((i) => i.section === section).map((i) => i.key),
    }))
    .filter((g) => g.keys.length > 0)
})

async function openExport() {
  popoverKey.value++
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
  <CoarPopover v-if="store.count > 0" :key="popoverKey" mode="click" :offset="8">
    <button type="button" class="export-chip" data-testid="export-chip"
      :title="t('admin.realmConfig.selection.show', {}, 'Show collected entries')"
      :aria-label="t('admin.realmConfig.selection.show', {}, 'Show collected entries')">
      <CoarIcon name="download" size="s" />
      <span class="chip-label">{{ t('admin.realmConfig.selection.title', {}, 'Export selection') }}</span>
      <span class="chip-count">{{ store.count }}</span>
    </button>
    <template #content>
      <div class="selection-panel" data-testid="export-chip-panel">
        <div class="panel-title">
          {{ t('admin.realmConfig.selection.count', { count: store.count }, `${store.count} collected`) }}
        </div>
        <div class="panel-list">
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
        <div class="panel-actions">
          <CoarButton size="s" variant="ghost" data-testid="export-chip-clear" @click="store.clear()">
            {{ t('admin.realmConfig.selection.clear', {}, 'Clear') }}
          </CoarButton>
          <CoarButton size="s" variant="secondary" icon-start="download" data-testid="export-chip-download" @click="openExport">
            {{ t('admin.realmConfig.selection.download', {}, 'Download as manifest…') }}
          </CoarButton>
        </div>
      </div>
    </template>
  </CoarPopover>
</template>

<style scoped>
/* Sits on the dark header next to the inbox bell — same translucent-white
   language as the bell and the avatar button. */
.export-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  height: 2rem;
  padding: 0 0.7rem;
  border: 1px solid rgba(255, 255, 255, 0.3);
  border-radius: 9999px;
  background: rgba(255, 255, 255, 0.08);
  color: white;
  font-size: 0.8rem;
  font-weight: 500;
  white-space: nowrap;
  cursor: pointer;
  transition: background 0.15s ease;
}

.export-chip:hover {
  background: rgba(255, 255, 255, 0.18);
}

.chip-count {
  min-width: 1.2rem;
  padding: 0 0.35rem;
  border-radius: 9999px;
  background: rgba(255, 255, 255, 0.22);
  font-size: 0.72rem;
  font-weight: 700;
  line-height: 1.2rem;
  text-align: center;
}

@media (max-width: 900px) {
  .chip-label {
    display: none;
  }
}

.selection-panel {
  display: flex;
  flex-direction: column;
  min-width: 280px;
  max-width: 380px;
}

.panel-title {
  padding: 8px 12px 4px;
  font-weight: 600;
  font-size: 0.8rem;
}

.panel-list {
  max-height: 320px;
  overflow-y: auto;
  padding: 0 4px 6px;
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

.panel-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  padding: 8px 10px;
  border-top: 1px solid var(--coar-border-neutral-subtle, #e5e7eb);
}
</style>
