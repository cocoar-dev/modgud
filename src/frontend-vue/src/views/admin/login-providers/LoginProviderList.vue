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
  // Built-in providers (Internal Authentication) ship with the IdP and
  // can't be removed/edited — dim the row to telegraph that.
  .rowClassRules({
    'is-system': (p: any) => p.data?.IsBuiltIn === true,
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
    // Inline grid toggle — immediate PATCH of just the Enabled property
    // (this is the "übers Grid" quick path; the edit modal stages it instead).
    await store.setEnabled(provider.Id, !provider.Enabled)
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

// "Add Provider" routes straight into the unified modal in Add mode (id='create').
// The modal hosts the flavor picker in its header-actions slot, so the admin
// fills in Type/Flavor + all other fields in one place and one Save click.
function openAddDialog() {
  navigateToModal('create')
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
  </div>
</template>
