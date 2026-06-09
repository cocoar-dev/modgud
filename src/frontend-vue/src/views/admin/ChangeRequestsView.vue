<script setup lang="ts">
import { ref, onMounted, watch, computed } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton, CoarCheckbox, CoarTextInput, CoarFormField, CoarNote } from '@cocoar/vue-ui'
import { useGridLocale } from '@/composables/useGridLocale'
import GridEmptyState from '@/components/GridEmptyState.vue'
import ModalLayout from '@/components/ModalLayout.vue'

const { t, language } = useI18n()
const { searchPlaceholder, applyListGridDefaults } = useGridLocale()
const http = useHttpClient('/api/admin/change-requests')

const ui = useUI()
watch(language, () => ui.set((ctx) => {
  ctx.header.title = t('nav.administration', {}, 'Administration')
  ctx.header.subTitle = t('admin.changeRequests.title', {}, 'Change requests')
  ctx.header.icon = 'inbox'
  ctx.content.container = false
}), { immediate: true })

interface ChangeItem { Field: string; OldValue: string | null; NewValue: string | null }
interface ChangeRequest {
  Id: string
  UserId: string
  UserLabel: string
  Type: string
  Status: 'EmailVerificationPending' | 'AdminApprovalPending' | 'Approved' | 'Rejected'
  Changes: ChangeItem[]
  RequestedAt: string
  UpdatedAt: string
  VerifiedAt: string | null
  ReviewedAt: string | null
  ReviewerNote: string | null
}

const typeLabels: Record<string, string> = {
  Profile: 'Profiländerung',
}

const fieldLabels: Record<string, string> = {
  Firstname: 'Vorname',
  Lastname: 'Nachname',
  Acronym: 'Kürzel',
  Email: 'E-Mail',
}

const requests = ref<ChangeRequest[]>([])
const loading = ref(true)
const includeTerminal = ref(false)

// Modal
const selected = ref<ChangeRequest | null>(null)
const rejectNote = ref('')
const notifyUser = ref(true)
const busy = ref(false)
const actionError = ref('')

async function loadRequests() {
  try {
    requests.value = await http
      .setQueryParameter('includeTerminal', String(includeTerminal.value))
      .get<ChangeRequest[]>()
  } catch { /* ignore */ }
  finally { loading.value = false }
}

watch(includeTerminal, () => loadRequests())

onMounted(loadRequests)

function openRow(row: ChangeRequest) {
  selected.value = row
  rejectNote.value = ''
  notifyUser.value = true
  actionError.value = ''
}

async function approve() {
  if (!selected.value || busy.value) return
  busy.value = true
  actionError.value = ''
  try {
    await http.addPath(selected.value.Id, 'approve').post({ NotifyUser: notifyUser.value })
    await loadRequests()
    selected.value = null
  } catch (e: any) {
    actionError.value = e?.body?.Message || t('admin.changeRequests.approveFailed', {}, 'Freigabe fehlgeschlagen.')
  } finally { busy.value = false }
}

async function reject() {
  if (!selected.value || busy.value) return
  busy.value = true
  actionError.value = ''
  try {
    await http.addPath(selected.value.Id, 'reject').post({
      Note: rejectNote.value.trim() || null,
      NotifyUser: notifyUser.value,
    })
    await loadRequests()
    selected.value = null
  } catch (e: any) {
    actionError.value = e?.body?.Message || t('admin.changeRequests.rejectFailed', {}, 'Ablehnung fehlgeschlagen.')
  } finally { busy.value = false }
}

const statusLabels = computed(() => ({
  EmailVerificationPending: t('admin.changeRequests.statusVerify', {}, 'Waiting for email confirmation'),
  AdminApprovalPending: t('admin.changeRequests.statusAdmin', {}, 'Wartet auf Freigabe'),
  Approved: t('admin.changeRequests.statusApproved', {}, 'Genehmigt'),
  Rejected: t('admin.changeRequests.statusRejected', {}, 'Abgelehnt'),
}))

const showEmpty = computed(() => !loading.value && requests.value.length === 0)

const gridBuilder = applyListGridDefaults(CoarGridBuilder.create<ChangeRequest>(), { openable: true })
  .rowDataRef(requests)
  .searchHighlight()
  .rowSelection('single')
  .onCellDoubleClicked((event) => { if (event.data) openRow(event.data) })
  .columns([
    (col) => col.date('UpdatedAt', { includeTime: true }).header('Zuletzt geändert', 'admin.changeRequests.updatedAt').width(170),
    (col) => col.field('UserLabel').header('Benutzer', 'admin.changeRequests.user').flex(1),
    (col) => col.field('Type').header('Typ', 'admin.changeRequests.type').width(140)
      .option('valueGetter', (p: any) => typeLabels[p.data?.Type as string] ?? p.data?.Type),
    (col) => col.field('Changes').header('Felder', 'admin.changeRequests.fields').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Changes ?? []).map((c: ChangeItem) => fieldLabels[c.Field] || c.Field).join(', ')),
    (col) => col.field('Status').header('Status', 'admin.changeRequests.status').width(180)
      .option('valueGetter', (p: any) => statusLabels.value[p.data?.Status as keyof typeof statusLabels.value] ?? p.data?.Status),
  ])
</script>

<template>
  <div class="flex flex-col min-h-0 flex-1 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="gridBuilder" :search-placeholder="searchPlaceholder" class="h-full" show-search bordered elevated>
      <template #toolbar-right>
        <CoarCheckbox v-model="includeTerminal"
          :label="t('admin.changeRequests.includeTerminal', {}, 'Auch erledigte anzeigen')" />
        <CoarButton size="s" variant="ghost" icon-start="rotate-ccw" @click="loadRequests">
          {{ t('common.refresh', {}, 'Neu laden') }}
        </CoarButton>
      </template>
    </CoarDataGrid>

    <GridEmptyState
      v-if="showEmpty"
      icon="inbox"
      :title="t('admin.changeRequests.title', {}, 'Change requests')"
      :description="t('admin.changeRequests.emptyHint', {}, 'When users request profile changes that need approval, they queue here for you to review. Nothing is waiting right now.')"
    />

    <!-- Details Modal -->
    <Teleport to="body">
      <div v-if="selected" class="fixed inset-0 z-[1000] flex items-center justify-center bg-black/40"
        @click.self="selected = null">
        <ModalLayout :close="() => selected = null" icon="inbox"
          :title="t('admin.changeRequests.reviewTitle', {}, 'Review request')" width="36rem">
          <div class="flex flex-col gap-4 p-2 text-sm">
            <div class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2">
              <div class="text-gray-600">{{ t('admin.changeRequests.user', {}, 'Benutzer') }}:</div>
              <div class="font-medium">{{ selected.UserLabel }}</div>
              <div class="text-gray-600">{{ t('admin.changeRequests.type', {}, 'Typ') }}:</div>
              <div class="font-medium">{{ typeLabels[selected.Type] || selected.Type }}</div>
              <div class="text-gray-600">{{ t('admin.changeRequests.status', {}, 'Status') }}:</div>
              <div>{{ statusLabels[selected.Status] }}</div>
              <div v-if="selected.VerifiedAt" class="text-gray-600">{{ t('admin.changeRequests.verifiedAt', {}, 'Email confirmed') }}:</div>
              <div v-if="selected.VerifiedAt">{{ new Date(selected.VerifiedAt).toLocaleString() }}</div>
              <div v-if="selected.ReviewerNote" class="text-gray-600">{{ t('admin.changeRequests.reviewerNote', {}, 'Reason') }}:</div>
              <div v-if="selected.ReviewerNote">{{ selected.ReviewerNote }}</div>
            </div>

            <div>
              <div class="text-xs font-semibold uppercase text-surface-500 mb-2">
                {{ t('admin.changeRequests.changes', {}, 'Changes') }}
              </div>
              <ul class="space-y-1">
                <li v-for="c in selected.Changes" :key="c.Field" class="flex gap-2">
                  <span class="text-gray-600 w-24">{{ fieldLabels[c.Field] || c.Field }}:</span>
                  <span class="line-through text-surface-400">{{ c.OldValue || '–' }}</span>
                  <span class="text-surface-500">→</span>
                  <span class="font-medium">{{ c.NewValue || '–' }}</span>
                </li>
              </ul>
            </div>

            <template v-if="selected.Status !== 'Approved' && selected.Status !== 'Rejected'">
              <CoarFormField :label="t('admin.changeRequests.rejectReason', {}, 'Ablehnungsgrund (optional)')">
                <CoarTextInput v-model="rejectNote"
                  :placeholder="t('admin.changeRequests.rejectReasonPlaceholder', {}, 'z.B. Adresse passt nicht zur Firma')" />
              </CoarFormField>
              <CoarCheckbox v-model="notifyUser"
                :label="t('admin.changeRequests.notifyUser', {}, 'Benutzer per E-Mail benachrichtigen')" />
              <CoarNote v-if="actionError" variant="error">{{ actionError }}</CoarNote>
              <div class="flex gap-2 justify-end pt-2">
                <CoarButton variant="danger" icon-start="x" :loading="busy"
                  :disabled="selected.Status === 'EmailVerificationPending'"
                  @click="reject">
                  {{ t('admin.changeRequests.reject', {}, 'Ablehnen') }}
                </CoarButton>
                <CoarButton variant="primary" icon-start="check" :loading="busy"
                  :disabled="selected.Status !== 'AdminApprovalPending'"
                  @click="approve">
                  {{ t('admin.changeRequests.approve', {}, 'Freigeben') }}
                </CoarButton>
              </div>
              <CoarNote v-if="selected.Status === 'EmailVerificationPending'" variant="info">
                {{ t('admin.changeRequests.waitingForVerify', {}, 'The user has not yet confirmed the new address via email. Approval is only possible once ownership has been proven.') }}
              </CoarNote>
            </template>
          </div>
        </ModalLayout>
      </div>
    </Teleport>
  </div>
</template>
