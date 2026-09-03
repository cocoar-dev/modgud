<script setup lang="ts">
/**
 * The export-selection bar — footer-positioned by MainLayout (right above the
 * draft staging bar) whenever the selection is non-empty. The admin collects
 * entities from the normal admin grids via their context menus ("Add to
 * export selection"), using the grids' full search/filter power; this bar
 * carries the count and the verbs: export (opens the selective-export review
 * modal pre-filled with the collection) and clear.
 */
import { CoarButton, CoarIcon, CoarTag, useToast } from '@cocoar/vue-ui'
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
    <CoarIcon name="list-checks" size="s" class="bar-icon" />
    <span class="bar-name">{{ t('admin.realmConfig.selection.title', {}, 'Export selection') }}</span>
    <CoarTag variant="info" size="s">
      {{ t('admin.realmConfig.selection.count', { count: store.count }, `${store.count} collected`) }}
    </CoarTag>

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

.bar-icon {
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
</style>
