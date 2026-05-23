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
import { useUserStore } from '@/stores/user.store'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import type { UserDto } from '@/models/user'
import SetPasswordModal from './SetPasswordModal.vue'

const { t, language } = useI18n()
useRoutedModals()
const { navigateToModal } = useFragmentNavigation()
const userStore = useUserStore()
const adminHttp = useHttpClient('/api/admin/users')

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.users.title', {}, 'Users')
  ctx.header.icon = 'users'
  ctx.content.container = false
}), { immediate: true })

const users = computed(() => userStore.entities)

const cellMenu = useContextMenu()
const viewportMenu = useContextMenu()
const selectedIds = ref<string[]>([])
const passwordModalUserId = ref<string | null>(null)

const selectedUser = computed(() => {
  const id = selectedIds.value[0]
  return id ? users.value.find(u => u.Id === id) : null
})

const builder = CoarGridBuilder.create<UserDto>()
  .persistColumnState('admin-users')
  .option('getRowId', (p: any) => p.data.Id)
  .rowDataRef(users)
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
    selectedIds.value = event.api.getSelectedRows().map((r: UserDto) => r.Id)
    cellMenu.open(event.event as MouseEvent)
  })
  .onViewportContextMenu(($event) => {
    viewportMenu.open($event)
  })
  .columns([
    // Password-set indicator — pinned first so the admin sees account-setup
    // state at a glance, even with many columns scrolled away.
    (col) => col.icon('HasPassword').header('')
      .valueGetter((p: any) => p.data?.HasPassword ? 'key-round' : '')
      .width(38).resizable(false).pinned('left'),
    // Identity column — pinned next to the password indicator and
    // emphasized as the row's primary label.
    (col) => col.field('UserName').header('Username', 'admin.users.username')
      .width(150).pinned('left').cellClass('user-name-cell'),
    (col) => col.field('Firstname').header('First Name', 'admin.users.firstname').flex(1),
    (col) => col.field('Lastname').header('Last Name', 'admin.users.lastname').flex(1),
    (col) => col.field('Acronym').header('Acronym', 'admin.users.acronym').width(100),
    (col) => col.icon('IsActive', { color: '#16a34a', size: 's' })
      .option('valueGetter', (p: any) => p.data?.IsActive ? 'check' : '')
      .header('Active', 'admin.users.active').width(80),
    // Email — visually de-emphasized when Identity-side EmailConfirmed=false
    // so unverified addresses don't read like authoritative contact info.
    (col) => col.field('Email').header('Email', 'admin.users.email').flex(1)
      .classRule('email-unverified', (p: any) => !!p.data?.Email && !p.data?.EmailConfirmed),
  ])

async function deleteUsers() {
  if (selectedIds.value.length > 0 && confirm(t('common.confirmDelete', {}, 'Really delete?'))) {
    await userStore.deleteEntities(selectedIds.value)
  }
}

const magicLinkSending = ref(false)
const magicLinkResult = ref<{ ok: boolean; message: string } | null>(null)

async function sendMagicLink() {
  const id = selectedIds.value[0]
  if (!id || magicLinkSending.value) return
  magicLinkSending.value = true
  magicLinkResult.value = null
  try {
    await adminHttp.addPath(id, 'magic-link').post()
    magicLinkResult.value = { ok: true, message: t('admin.users.magicLinkSent', {}, 'Magic link sent.') }
  } catch (e: any) {
    magicLinkResult.value = { ok: false, message: e?.data?.Message || t('admin.users.magicLinkFailed', {}, 'Failed to send magic link.') }
  } finally {
    magicLinkSending.value = false
    setTimeout(() => magicLinkResult.value = null, 5000)
  }
}

onMounted(() => {
  userStore.initialize()
})
</script>

<template>
  <div class="flex flex-1 flex-col min-w-0 p-4">
    <CoarDataGrid :builder="builder" show-search class="flex-1 min-h-0" bordered elevated>
      <template #toolbar-right>
        <CoarButton size="s" icon-start="plus" @click="navigateToModal('create')">{{ t('common.create', {}, 'Create') }}</CoarButton>
      </template>
    </CoarDataGrid>

    <!-- Magic link result toast -->
    <div v-if="magicLinkResult" class="fixed bottom-4 right-4 z-50 rounded-lg px-4 py-3 text-sm shadow-lg"
      :class="magicLinkResult.ok ? 'bg-green-100 text-green-800' : 'bg-red-100 text-red-800'">
      {{ magicLinkResult.message }}
    </div>

    <!-- Row context menu -->
    <CoarContextMenu :menu="cellMenu">
      <CoarMenuItem :label="t('common.open', {}, 'Open')" icon="pencil" @clicked="selectedIds[0] && navigateToModal(selectedIds[0])" />
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('admin.users.setPassword', {}, 'Set Password')" icon="key" @clicked="passwordModalUserId = selectedIds[0] ?? null" />
      <CoarMenuItem
        :label="t('admin.users.sendMagicLink', {}, 'Send Magic Link')"
        icon="mail"
        :disabled="!selectedUser?.Email"
        @clicked="sendMagicLink"
      />
      <CoarMenuItem
        :label="t('admin.users.showIdpClaims', {}, 'Show IdP Claims')"
        icon="key-round"
        @clicked="selectedIds[0] && navigateToModal(`claims/${selectedIds[0]}`)"
      />
      <CoarMenuDivider />
      <CoarMenuItem :label="t('common.delete', {}, 'Delete')" icon="trash-2" @clicked="deleteUsers" />
    </CoarContextMenu>

    <!-- Viewport context menu (empty area) -->
    <CoarContextMenu :menu="viewportMenu">
      <CoarMenuItem :label="t('common.create', {}, 'Create')" icon="plus" @clicked="navigateToModal('create')" />
    </CoarContextMenu>

    <!-- Password modal (non-routed) -->
    <Teleport to="body">
      <div v-if="passwordModalUserId" class="password-modal-overlay" @click.self="passwordModalUserId = null">
        <SetPasswordModal :id="passwordModalUserId" :close="() => passwordModalUserId = null" />
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.password-modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 1000;
  display: flex;
  align-items: center;
  justify-content: center;
  background-color: rgba(0, 0, 0, 0.4);
}

/* AG Grid cells render inside the host component, so style hooks need
   :deep() to reach them from scoped styles. */
:deep(.user-name-cell) {
  font-weight: 600;
}

:deep(.email-unverified) {
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-style: italic;
}
</style>
