<script setup lang="ts">
import { ref, onMounted, watch, computed } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUI } from '@/composables/useUI'
import { useI18n } from '@cocoar/vue-localization'
import { CoarDataGrid, CoarGridBuilder } from '@cocoar/vue-data-grid'
import { CoarButton, CoarCheckbox, CoarTextInput, CoarFormField } from '@cocoar/vue-ui'
import { useGridLocale } from '@/composables/useGridLocale'
import GridEmptyState from '@/components/GridEmptyState.vue'
import ModalLayout from '@/components/ModalLayout.vue'
import Notice from '@/components/Notice.vue'

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

const typeLabels = computed<Record<string, string>>(() => ({
  Profile: t('admin.changeRequests.typeLabels.profile', {}, 'Profile Change'),
}))

const fieldLabels = computed<Record<string, string>>(() => ({
  Firstname: t('admin.changeRequests.fieldLabels.firstname', {}, 'First Name'),
  Lastname: t('admin.changeRequests.fieldLabels.lastname', {}, 'Last Name'),
  Acronym: t('admin.changeRequests.fieldLabels.acronym', {}, 'Acronym'),
  Email: t('admin.changeRequests.fieldLabels.email', {}, 'Email'),
}))

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
    actionError.value = e?.body?.Message || t('admin.changeRequests.approveFailed', {}, 'Approval failed.')
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
    actionError.value = e?.body?.Message || t('admin.changeRequests.rejectFailed', {}, 'Rejection failed.')
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
    (col) => col.date('UpdatedAt', { includeTime: true }).header('Last changed', 'admin.changeRequests.updatedAt').width(170),
    (col) => col.field('UserLabel').header('User', 'admin.changeRequests.user').flex(1),
    (col) => col.field('Type').header('Type', 'admin.changeRequests.type').width(140)
      .option('valueGetter', (p: any) => typeLabels.value[p.data?.Type as string] ?? p.data?.Type),
    (col) => col.field('Changes').header('Fields', 'admin.changeRequests.fields').flex(1)
      .option('valueGetter', (p: any) => (p.data?.Changes ?? []).map((c: ChangeItem) => fieldLabels.value[c.Field] || c.Field).join(', ')),
    (col) => col.field('Status').header('Status', 'admin.changeRequests.status').width(180)
      .option('valueGetter', (p: any) => statusLabels.value[p.data?.Status as keyof typeof statusLabels.value] ?? p.data?.Status),
  ])
</script>

<template>
  <div class="flex flex-col min-h-0 flex-1 p-4">
    <CoarDataGrid v-show="!showEmpty" :builder="gridBuilder" :search-placeholder="searchPlaceholder" class="h-full" show-search bordered elevated>
      <template #toolbar-right>
        <CoarCheckbox v-model="includeTerminal"
          :label="t('admin.changeRequests.includeTerminal', {}, 'Also show completed')" />
        <CoarButton size="s" variant="ghost" icon-start="rotate-ccw" @click="loadRequests">
          {{ t('common.refresh', {}, 'Refresh') }}
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
              <div class="text-gray-600">{{ t('admin.changeRequests.user', {}, 'User') }}:</div>
              <div class="font-medium">{{ selected.UserLabel }}</div>
              <div class="text-gray-600">{{ t('admin.changeRequests.type', {}, 'Type') }}:</div>
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
              <CoarFormField :label="t('admin.changeRequests.rejectReason', {}, 'Rejection reason (optional)')">
                <CoarTextInput v-model="rejectNote"
                  :placeholder="t('admin.changeRequests.rejectReasonPlaceholder', {}, 'e.g. address doesn\'t match the company')" />
              </CoarFormField>
              <CoarCheckbox v-model="notifyUser"
                :label="t('admin.changeRequests.notifyUser', {}, 'Notify user by email')" />
              <Notice v-if="actionError" variant="error">{{ actionError }}</Notice>
              <div class="flex gap-2 justify-end pt-2">
                <CoarButton variant="danger" icon-start="x" :loading="busy"
                  :disabled="selected.Status === 'EmailVerificationPending'"
                  @click="reject">
                  {{ t('admin.changeRequests.reject', {}, 'Reject') }}
                </CoarButton>
                <CoarButton variant="primary" icon-start="check" :loading="busy"
                  :disabled="selected.Status !== 'AdminApprovalPending'"
                  @click="approve">
                  {{ t('admin.changeRequests.approve', {}, 'Approve') }}
                </CoarButton>
              </div>
              <Notice v-if="selected.Status === 'EmailVerificationPending'" variant="info">
                {{ t('admin.changeRequests.waitingForVerify', {}, 'The user has not yet confirmed the new address via email. Approval is only possible once ownership has been proven.') }}
              </Notice>
            </template>
          </div>
        </ModalLayout>
      </div>
    </Teleport>
  </div>
</template>
