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
import { useLoginProviderStore } from '@/stores/loginProvider.store'
import { useUI } from '@/composables/useUI'
import type { LoginProviderDto, LoginProviderType } from '@/models/loginProvider'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const store = useLoginProviderStore()

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.loginProviders.title', {}, 'Login-Provider')
  ctx.header.icon = 'log-in'
  ctx.content.container = false
}), { immediate: true })

const rows = computed(() => store.providers)
const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])

// i18n helper for the Type column.
function typeLabel(type: LoginProviderType): string {
  switch (type) {
    case 'Internal': return t('admin.loginProviders.type.values.internal', {}, 'Intern')
    case 'Oidc': return t('admin.loginProviders.type.values.oidc', {}, 'OIDC')
    case 'Saml': return t('admin.loginProviders.type.values.saml', {}, 'SAML')
    case 'Ldap': return t('admin.loginProviders.type.values.ldap', {}, 'LDAP')
    case 'Kerberos': return t('admin.loginProviders.type.values.kerberos', {}, 'Kerberos')
    default: return type
  }
}

const builder = CoarGridBuilder.create<LoginProviderDto>()
  .persistColumnState('admin-login-providers')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(rows)
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
    selectedIds.value = event.api.getSelectedRows().map((r: LoginProviderDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => viewportMenu.open($event))
  .columns([
    (col) => col.icon('IconName', { size: 's' })
      .option('valueGetter', (p: any) => p.data?.IconName ?? (p.data?.Type === 'Internal' ? 'lock' : 'key-round'))
      .header('').width(48).resizable(false),
    (col) => col.field('DisplayName').header('Name', 'admin.loginProviders.displayName').flex(2),
    (col) => col.field('Type').header('Typ', 'admin.loginProviders.type.label').width(120)
      .option('valueGetter', (p: any) => {
        if (!p.data) return ''
        const base = typeLabel(p.data.Type as LoginProviderType)
        return p.data.IsBuiltIn
          ? `${base} · ${t('admin.loginProviders.builtIn.badge', {}, 'System')}`
          : base
      }),
    (col) => col.field('Flavor').header('Flavor', 'admin.loginProviders.flavor').width(140),
    (col) => col.field('Enabled').header('Aktiv', 'admin.loginProviders.enabled').width(100)
      .option('valueGetter', (p: any) => p.data?.Enabled
        ? t('common.yes', {}, 'Ja')
        : t('common.no', {}, 'Nein')),
    (col) => col.field('ClientId').header('Client ID', 'admin.loginProviders.clientId').flex(2),
    (col) => col.field('HasClientSecret').header('Secret', 'admin.loginProviders.hasSecret').width(100)
      .option('valueGetter', (p: any) => p.data?.HasClientSecret ? '••••••' : '—'),
    (col) => col.date('UpdatedAt', { includeTime: true }).header('Aktualisiert', 'admin.loginProviders.updatedAt').width(170),
  ])

const selectedProvider = computed(() => rows.value.find((p) => p.Id === selectedIds.value[0]))

async function toggleEnabled() {
  const provider = selectedProvider.value
  if (!provider) return
  if (provider.IsBuiltIn) {
    alert(t('admin.loginProviders.errors.internalNotEditable', {}, 'Der eingebaute interne Login-Provider kann nicht bearbeitet werden.'))
    return
  }
  try {
    if (provider.Enabled) await store.disable(provider.Id)
    else await store.enable(provider.Id)
  } catch (e: any) {
    alert(e?.message ?? String(e))
  }
}

async function deleteSelected() {
  const provider = selectedProvider.value
  if (!provider) return
  if (provider.IsBuiltIn) {
    alert(t('admin.loginProviders.errors.internalNotEditable', {}, 'Der eingebaute interne Login-Provider kann nicht bearbeitet werden.'))
    return
  }
  if (!confirm(t('admin.loginProviders.confirmDelete', {}, 'Diesen Login-Provider wirklich löschen?'))) return
  try { await store.remove(provider.Id) } catch (e: any) { alert(e?.message ?? String(e)) }
}

// ─── "Add Provider" flow ──────────────────────────────────────────────
// Today only Oidc providers are creatable from the UI. The picker therefore
// only shows the registered OIDC flavors. When Saml/Ldap/Kerberos support
// lands the type select returns and the flavor picker reacts to it.
const addOpen = ref(false)
const addForm = ref<{ Flavor: string; DisplayName: string }>({ Flavor: '', DisplayName: '' })
const addError = ref<string | null>(null)
const flavorOptions = computed(() =>
  store.flavors.map((f) => ({ value: f.Key, label: f.DisplayName }))
)

function openAddDialog() {
  addForm.value = { Flavor: store.flavors[0]?.Key ?? '', DisplayName: '' }
  addError.value = null
  addOpen.value = true
}

async function confirmAdd() {
  if (!addForm.value.DisplayName.trim()) {
    addError.value = t('admin.loginProviders.nameRequired', {}, 'Name ist erforderlich')
    return
  }
  addError.value = null
  try {
    const created = await store.create({
      Flavor: addForm.value.Flavor,
      DisplayName: addForm.value.DisplayName.trim(),
      Type: 'Oidc',
      FlavorData: placeholderFlavorData(addForm.value.Flavor),
    })
    addOpen.value = false
    navigateToModal(created.Id)
  } catch (e: any) {
    addError.value = e?.response?.data?.Message ?? e?.body?.Message ?? e?.message ?? String(e)
  }
}

function placeholderFlavorData(flavorKey: string): Record<string, unknown> {
  // Minimum viable payload so flavor validation passes at Create time —
  // real values are supplied on the details modal.
  const flavor = store.flavors.find((f) => f.Key === flavorKey)
  const result: Record<string, unknown> = {}
  for (const field of flavor?.ConfigSchema ?? []) {
    if (field.Required) result[field.Key] = '00000000-0000-0000-0000-000000000000'
  }
  return result
}

onMounted(() => store.initialize())
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid :builder="builder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="openAddDialog">
          {{ t('admin.loginProviders.add', {}, 'Provider hinzufügen') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <!-- Row context menu -->
    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Öffnen')" icon="pencil"
        @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem
        v-if="selectedProvider"
        :label="selectedProvider.Enabled
          ? t('admin.loginProviders.disable', {}, 'Deaktivieren')
          : t('admin.loginProviders.enable', {}, 'Aktivieren')"
        :icon="selectedProvider.Enabled ? 'circle-pause' : 'circle-play'"
        :disabled="selectedProvider.IsBuiltIn"
        @clicked="toggleEnabled"
      />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Löschen')" icon="trash-2"
        :disabled="!selectedProvider || selectedProvider.IsBuiltIn"
        @clicked="deleteSelected" />
    </CoarContextMenu>

    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('admin.loginProviders.add', {}, 'Provider hinzufügen')" icon="plus"
        @clicked="openAddDialog" />
    </CoarContextMenu>

    <!-- Add provider inline dialog — flips in above the grid -->
    <Transition name="fade">
      <div v-if="addOpen" class="add-overlay" @click.self="addOpen = false">
        <div class="add-dialog">
          <h3 class="add-title">{{ t('admin.loginProviders.addTitle', {}, 'Login-Provider hinzufügen') }}</h3>
          <CoarFormField :label="t('admin.loginProviders.flavor', {}, 'Flavor')">
            <CoarSelect v-model="addForm.Flavor" :options="flavorOptions" />
          </CoarFormField>
          <CoarFormField :label="t('admin.loginProviders.displayName', {}, 'Name')">
            <CoarTextInput v-model="addForm.DisplayName" placeholder="Acme Corp Entra" clearable />
          </CoarFormField>
          <div v-if="addError" class="text-sm text-red-600">{{ addError }}</div>
          <div class="flex justify-end gap-2 mt-3">
            <CoarButton variant="subtle" size="s" @click="addOpen = false">{{ t('common.cancel', {}, 'Abbrechen') }}</CoarButton>
            <CoarButton size="s" @click="confirmAdd">{{ t('common.create', {}, 'Erstellen') }}</CoarButton>
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
