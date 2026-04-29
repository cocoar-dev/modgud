<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import {
  CoarButton,
  useContextMenu,
  CoarContextMenu,
  CoarMenuItem,
  CoarMenuDivider,
  CoarFormField,
  CoarSelect,
  CoarTextInput,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useFragmentNavigation, useRoutedModals } from '@cocoar/vue-fragment-parser'
import { useIdpConfigStore } from '@/stores/idpConfig.store'
import { useUI } from '@/composables/useUI'
import type { IdpConfigDto } from '@/models/idpConfig'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const idpStore = useIdpConfigStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.idpConfig.title', {}, 'Identity Providers')
  ctx.header.icon = 'key-round'
  ctx.content.container = false
}), { immediate: true })

const configs = computed(() => idpStore.configs)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

const builder = CoarGridBuilder.create<IdpConfigDto>()
  .persistColumnState('admin-idp-config')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(configs)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event) => {
    if (event.data) navigateToModal(event.data.Id)
  })
  .onCellContextMenu((event) => {
    if (!event.node.isSelected()) {
      event.api.deselectAll()
      event.node.setSelected(true)
    }
    selectedIds.value = event.api.getSelectedRows().map((r: IdpConfigDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.field('DisplayName').header('Name', 'admin.idpConfig.name').flex(2),
    (col) => col.field('Flavor').header('Type', 'admin.idpConfig.flavor').width(140),
    (col) => col.field('Enabled').header('Enabled', 'admin.idpConfig.enabled').width(110)
      .option('valueGetter', (p: any) => p.data?.Enabled
        ? t('common.yes', {}, 'Yes')
        : t('common.no', {}, 'No')),
    (col) => col.field('ClientId').header('Client ID', 'admin.idpConfig.clientId').flex(2),
    (col) => col.field('HasClientSecret').header('Secret', 'admin.idpConfig.hasSecret').width(100)
      .option('valueGetter', (p: any) => p.data?.HasClientSecret ? '••••••' : '—'),
    (col) => col.date('UpdatedAt', { includeTime: true }).header('Updated', 'admin.idpConfig.updatedAt').width(170),
  ])

const selectedConfig = computed(() => configs.value.find((c) => c.Id === selectedIds.value[0]))

async function toggleEnabled() {
  const config = selectedConfig.value
  if (!config) return
  try {
    if (config.Enabled) await idpStore.disable(config.Id)
    else await idpStore.enable(config.Id)
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
}

async function deleteSelected() {
  const id = selectedIds.value[0]
  if (!id) return
  if (!confirm(t('admin.idpConfig.confirmDelete', {}, 'Really delete this IdP configuration?'))) return
  try { await idpStore.remove(id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

// ─── "Add Provider" flow ──────────────────────────────────────────────
const addOpen = ref(false)
const addForm = ref<{ Flavor: string; DisplayName: string }>({ Flavor: '', DisplayName: '' })
const addError = ref<string | null>(null)
const flavorOptions = computed(() =>
  idpStore.flavors.map((f) => ({ value: f.Key, label: f.DisplayName }))
)

function openAddDialog() {
  addForm.value = { Flavor: idpStore.flavors[0]?.Key ?? '', DisplayName: '' }
  addError.value = null
  addOpen.value = true
}

async function confirmAdd() {
  if (!addForm.value.DisplayName.trim()) {
    addError.value = t('admin.idpConfig.nameRequired', {}, 'Name is required')
    return
  }
  addError.value = null
  try {
    const created = await idpStore.create({
      Flavor: addForm.value.Flavor,
      DisplayName: addForm.value.DisplayName.trim(),
      FlavorData: placeholderFlavorData(addForm.value.Flavor),
    })
    addOpen.value = false
    navigateToModal(created.Id)
  } catch (e: any) {
    addError.value = e?.response?.data?.Message ?? e?.message ?? String(e)
  }
}

function placeholderFlavorData(flavorKey: string): Record<string, unknown> {
  // Minimum viable payload so flavor validation passes at Create time —
  // real values are supplied on the details modal.
  const flavor = idpStore.flavors.find((f) => f.Key === flavorKey)
  const result: Record<string, unknown> = {}
  for (const field of flavor?.ConfigSchema ?? []) {
    if (field.Required) result[field.Key] = '00000000-0000-0000-0000-000000000000'
  }
  return result
}

onMounted(() => idpStore.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid :builder="builder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="openAddDialog">
          {{ t('admin.idpConfig.add', {}, 'Add provider') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <!-- Row context menu -->
    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem
        v-if="selectedConfig"
        :label="selectedConfig.Enabled ? t('admin.idpConfig.disable', {}, 'Disable') : t('admin.idpConfig.enable', {}, 'Enable')"
        :icon="selectedConfig.Enabled ? 'circle-pause' : 'circle-play'"
        @clicked="toggleEnabled"
      />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('admin.idpConfig.add', {}, 'Add provider')" icon="plus" @clicked="openAddDialog" />
    </CoarContextMenu>

    <!-- Add provider inline dialog — flips in above the grid -->
    <Transition name="fade">
      <div v-if="addOpen" class="add-overlay" @click.self="addOpen = false">
        <div class="add-dialog">
          <h3 class="add-title">{{ t('admin.idpConfig.addTitle', {}, 'Add identity provider') }}</h3>
          <CoarFormField :label="t('admin.idpConfig.flavor', {}, 'Type')">
            <CoarSelect v-model="addForm.Flavor" :options="flavorOptions" />
          </CoarFormField>
          <CoarFormField :label="t('admin.idpConfig.name', {}, 'Name')">
            <CoarTextInput v-model="addForm.DisplayName" placeholder="Acme Corp Entra" clearable />
          </CoarFormField>
          <div v-if="addError" class="text-sm text-red-600">{{ addError }}</div>
          <div class="flex justify-end gap-2 mt-3">
            <CoarButton variant="subtle" size="s" @click="addOpen = false">{{ t('common.cancel', {}, 'Cancel') }}</CoarButton>
            <CoarButton size="s" @click="confirmAdd">{{ t('common.create', {}, 'Create') }}</CoarButton>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
.add-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.35);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 50;
}
.add-dialog {
  background: var(--coar-background-neutral-primary, #fff);
  border-radius: 8px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.15);
  padding: 20px;
  width: 28rem;
  max-width: 90vw;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.add-title {
  margin: 0 0 4px;
  font-size: 1rem;
  font-weight: 600;
}
.fade-enter-active, .fade-leave-active { transition: opacity 0.15s ease; }
.fade-enter-from, .fade-leave-to { opacity: 0; }
</style>
