<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { usePositionStore } from '@/stores/position.store'
import {
  CoarNotice,
  CoarTextInput,
  CoarNumberInput,
  CoarFormField,
  CoarCheckbox,
  CoarDivider,
  CoarSelect,
  CoarButton,
  CoarTag,
  CoarPopconfirm,
  CoarTabGroup,
  CoarTab,
  CoarIcon,
  CoarPopover,
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUserStore } from '@/stores/user.store'
import type { PositionCreateDto, PositionUpdateDto, PositionTerminalPolicyUpdateDto, PositionGrantDto, TerminalDto, StaffingSessionDto } from '@/models/position'

const { t } = useI18n()
const toast = useToast()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = usePositionStore()
const userStore = useUserStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)
// Modal-contract rule 5: create and edit share the layout — the sessions tab
// is simply absent while the position does not exist yet.
const activeTab = ref<'general' | 'terminals' | 'grants' | 'sessions'>('general')

const form = ref({
  AccountName: '',
  Purpose: '',
  IsActive: true,
  TerminalEnabled: false,
  // Plan defaults: a 16 h shift session under a 24 h absolute ceiling.
  StaffingSessionLifetimeMinutes: 16 * 60,
  MaximumStaffingSessionLifetimeMinutes: 24 * 60,
})
const original = ref({ ...form.value })
const accountNamePattern = /^[a-z0-9][a-z0-9._-]{1,63}$/

const accountNameError = computed(() => {
  const value = form.value.AccountName.trim()
  if (!value || !isCreate.value) return ''
  if (!accountNamePattern.test(value))
    return t(
      'admin.positions.accountNameInvalid',
      {},
      '2–64 characters; only lowercase letters, digits, dot, hyphen, and underscore.',
    )
  return ''
})

const lifetimeError = computed(() => {
  const session = form.value.StaffingSessionLifetimeMinutes
  const maximum = form.value.MaximumStaffingSessionLifetimeMinutes
  if (session <= 0 || maximum <= 0)
    return t('admin.positions.lifetimePositive', {}, 'Lifetimes must be positive.')
  if (session > maximum)
    return t('admin.positions.lifetimeCeiling', {}, 'The session lifetime must not exceed the absolute maximum.')
  return ''
})

const modalTitle = computed(() => {
  return isCreate.value
    ? t('admin.positions.createTitle', {}, 'Create position')
    : (form.value.AccountName || t('admin.positions.editTitle', {}, 'Position'))
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.AccountName.trim() || generalIssues.value.length > 0
    || terminalIssues.value.length > 0 || loading.value,
  onClick: save,
}))

// ── Activation grants (MG-FT-02) — edit-mode only. Operations, not staged
// edits (modal-contract rule 2): grants have their own lifecycle and audit
// identity, mirroring the SA credentials tab, so issue/suspend/resume/revoke
// act immediately with explicit buttons, apart from the primary Save.
const grants = ref<PositionGrantDto[]>([])
const grantsLoading = ref(false)
const selectedGrantUserId = ref<string | null>(null)
const grantsHttp = computed(() => useHttpClient(`/api/position/${props.id}/grants`))

// Create mode stages grants (rule 5: the entity is creatable completely — the
// one Save commits position + grants atomically); edit mode operates on live
// grants immediately (rule 2, they have their own lifecycle + audit identity).
const stagedGrantUserIds = ref<string[]>([])

function userLabel(userId: string): string {
  const u = userStore.entities.find((x) => x.Id === userId)
  return u ? (`${u.Firstname ?? ''} ${u.Lastname ?? ''}`.trim() || u.Email || userId) : userId
}

const grantableUserOptions = computed(() => {
  const taken = isCreate.value
    ? new Set(stagedGrantUserIds.value)
    : new Set(grants.value.filter((g) => g.Status !== 'Revoked').map((g) => g.UserId))
  return userStore.entities
    .filter((u) => u.IsActive && !taken.has(u.Id))
    .map((u) => ({ value: u.Id, label: userLabel(u.Id) }))
})

function stageGrant() {
  if (!selectedGrantUserId.value) return
  stagedGrantUserIds.value.push(selectedGrantUserId.value)
  selectedGrantUserId.value = null
}

function unstageGrant(userId: string) {
  stagedGrantUserIds.value = stagedGrantUserIds.value.filter((id) => id !== userId)
}

async function loadGrants() {
  if (isCreate.value) return
  grantsLoading.value = true
  try {
    grants.value = await grantsHttp.value.get<PositionGrantDto[]>()
  } finally {
    grantsLoading.value = false
  }
}

async function issueGrant() {
  if (!selectedGrantUserId.value) return
  try {
    await grantsHttp.value.post({ UserId: selectedGrantUserId.value })
    selectedGrantUserId.value = null
    await loadGrants()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function transitionGrant(grant: PositionGrantDto, action: 'suspend' | 'resume' | 'revoke') {
  try {
    await grantsHttp.value.addPath(grant.Id, action).post()
    await loadGrants()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

// ── Terminal slots (MG-FT-03). Create mode STAGES slots the same way it
// stages grants (rule 5: the entity is creatable completely — mirrors the
// service account's initial credential, which is staged into the same
// atomic create). Edit mode operates on live slots immediately (rule 2),
// where the PERSISTED policy has to allow them.
const terminals = ref<TerminalDto[]>([])
const terminalsLoading = ref(false)
const newTerminal = ref({ DisplayName: '', Location: '', WebAuthnRpId: '' })
const terminalsHttp = computed(() => useHttpClient(`/api/position/${props.id}/terminals`))
const stagedTerminals = ref<{ DisplayName: string; Location: string; WebAuthnRpId: string }[]>([])
// In create the staged policy decides (it is committed in the same save); in
// edit only the persisted one does, because the server validates against it.
const canAddTerminal = computed(() =>
  isCreate.value ? form.value.TerminalEnabled : original.value.TerminalEnabled)

// Passkeys hang off the RP ID, not the client: the existing hardware tokens of
// this position unlock a new slot only when it carries the SAME RP ID. So once
// any slot exists (live or staged), further slots inherit its RP ID and the
// field locks.
const existingRpId = computed(() =>
  terminals.value.find((slot) => slot.Status !== 'Revoked')?.WebAuthnRpId
  ?? stagedTerminals.value[0]?.WebAuthnRpId
  ?? '')

watch(existingRpId, (rpId) => {
  if (rpId) newTerminal.value.WebAuthnRpId = rpId
}, { immediate: true })

function stageTerminal() {
  if (!newTerminal.value.DisplayName.trim() || !newTerminal.value.WebAuthnRpId.trim()) return
  stagedTerminals.value.push({
    DisplayName: newTerminal.value.DisplayName.trim(),
    Location: newTerminal.value.Location.trim(),
    WebAuthnRpId: newTerminal.value.WebAuthnRpId.trim(),
  })
  // Keep the RP ID — every terminal of one consuming app shares it.
  newTerminal.value = { DisplayName: '', Location: '', WebAuthnRpId: newTerminal.value.WebAuthnRpId }
}

function unstageTerminal(index: number) {
  stagedTerminals.value.splice(index, 1)
}

// Tabs organize, they don't disclose (rule 1) — so a validation error on an
// inactive tab is flagged on its label, otherwise a disabled Save would have
// no visible cause. Same shape as GroupDetails.
const generalIssues = computed(() => [accountNameError.value].filter(Boolean) as string[])
const terminalIssues = computed(() => {
  const issues = [lifetimeError.value].filter(Boolean) as string[]
  if (stagedTerminals.value.length > 0 && !form.value.TerminalEnabled)
    issues.push(t('admin.positionTerminals.stagedNeedPolicy', {},
      'Turn terminal use on — the staged slots are saved with it.'))
  return issues
})

async function loadTerminals() {
  if (isCreate.value) return
  terminalsLoading.value = true
  try {
    terminals.value = await terminalsHttp.value.get<TerminalDto[]>()
  } finally {
    terminalsLoading.value = false
  }
}

async function createTerminal() {
  if (!newTerminal.value.DisplayName.trim() || !newTerminal.value.WebAuthnRpId.trim()) return
  try {
    await terminalsHttp.value.post({
      DisplayName: newTerminal.value.DisplayName.trim(),
      Location: newTerminal.value.Location.trim() || undefined,
      WebAuthnRpId: newTerminal.value.WebAuthnRpId.trim(),
    })
    newTerminal.value = { DisplayName: '', Location: '', WebAuthnRpId: newTerminal.value.WebAuthnRpId }
    await loadTerminals()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

async function transitionTerminal(terminal: TerminalDto, action: 'disable' | 'reactivate' | 'revoke') {
  try {
    await terminalsHttp.value.addPath(terminal.Id, action).post()
    await loadTerminals()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

// ── Staffing sessions (MG-FT-05/07) — read + force-lock, edit-mode only.
// The list is the admin's live view of who staffed which terminal when;
// force-lock ends a running shift remotely (§15.3).
const staffingSessions = ref<StaffingSessionDto[]>([])
const sessionsLoading = ref(false)
const sessionsHttp = computed(() => useHttpClient(`/api/position/${props.id}/staffing-sessions`))

async function loadStaffingSessions() {
  if (isCreate.value) return
  sessionsLoading.value = true
  try {
    staffingSessions.value = await sessionsHttp.value.get<StaffingSessionDto[]>()
  } catch {
    // staffing-session:read may be missing — the section simply
    // stays empty rather than breaking the modal.
    staffingSessions.value = []
  } finally {
    sessionsLoading.value = false
  }
}

async function forceLockSession(sessionId: string) {
  try {
    await useHttpClient(`/api/staffing-session/${sessionId}/force-lock`).post()
    await Promise.all([loadStaffingSessions(), loadTerminals()])
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

function terminalLabel(terminalId: string): string {
  return terminals.value.find((x) => x.Id === terminalId)?.DisplayName ?? terminalId
}

function endReasonLabel(reason: string | null | undefined): string {
  switch (reason) {
    case 'LocalLock': return t('admin.staffingSessions.reason.localLock', {}, 'Locked at the terminal')
    case 'RemoteLock': return t('admin.staffingSessions.reason.remoteLock', {}, 'Force-locked by an admin')
    case 'ReplacedByNewActivation': return t('admin.staffingSessions.reason.replaced', {}, 'Replaced by a new tap')
    case 'Expired': return t('admin.staffingSessions.reason.expired', {}, 'Shift ceiling reached')
    case 'PositionDisabled': return t('admin.staffingSessions.reason.positionDisabled', {}, 'Position deactivated')
    case 'TerminalDisabled': return t('admin.staffingSessions.reason.terminalDisabled', {}, 'Terminal disabled')
    case 'TerminalRevoked': return t('admin.staffingSessions.reason.terminalRevoked', {}, 'Terminal revoked')
    case 'UserDisabled': return t('admin.staffingSessions.reason.userDisabled', {}, 'User deactivated')
    case 'PasskeyDeleted': return t('admin.staffingSessions.reason.passkeyDeleted', {}, 'Passkey deleted')
    case 'GrantSuspended': return t('admin.staffingSessions.reason.grantSuspended', {}, 'Grant suspended')
    case 'GrantRevoked': return t('admin.staffingSessions.reason.grantRevoked', {}, 'Grant revoked')
    case 'OAuthClientDisabled': return t('admin.staffingSessions.reason.clientDisabled', {}, 'OAuth client disabled')
    default: return reason ?? ''
  }
}

function formatTime(value: string | null | undefined): string {
  return value ? new Date(value).toLocaleString() : ''
}

onMounted(async () => {
  // The user list feeds the grant picker in BOTH modes.
  void userStore.initialize()
  if (!isCreate.value) {
    loading.value = true
    try {
      const fn = await store.getById(props.id)
      form.value = {
        AccountName: fn.AccountName,
        Purpose: fn.Purpose ?? '',
        IsActive: fn.IsActive,
        TerminalEnabled: fn.TerminalPolicy.Enabled,
        StaffingSessionLifetimeMinutes: fn.TerminalPolicy.StaffingSessionLifetimeMinutes,
        MaximumStaffingSessionLifetimeMinutes: fn.TerminalPolicy.MaximumStaffingSessionLifetimeMinutes,
      }
      original.value = { ...form.value }
      // Grants + terminals + sessions load alongside — they must not block the form fields.
      void loadGrants()
      void loadTerminals()
      void loadStaffingSessions()
    } catch (e: unknown) {
      const err = e as { data?: { Message?: string }; message?: string }
      error.value = err?.data?.Message ?? err?.message ?? String(e)
    } finally {
      loading.value = false
    }
  }
})

function policyDiff(): PositionTerminalPolicyUpdateDto | undefined {
  const diff: PositionTerminalPolicyUpdateDto = {}
  if (form.value.TerminalEnabled !== original.value.TerminalEnabled)
    diff.Enabled = form.value.TerminalEnabled
  if (form.value.StaffingSessionLifetimeMinutes !== original.value.StaffingSessionLifetimeMinutes)
    diff.StaffingSessionLifetimeMinutes = form.value.StaffingSessionLifetimeMinutes
  if (form.value.MaximumStaffingSessionLifetimeMinutes !== original.value.MaximumStaffingSessionLifetimeMinutes)
    diff.MaximumStaffingSessionLifetimeMinutes = form.value.MaximumStaffingSessionLifetimeMinutes
  return Object.keys(diff).length > 0 ? diff : undefined
}

async function save() {
  if (!form.value.AccountName.trim() || accountNameError.value || lifetimeError.value) return
  loading.value = true
  error.value = null
  try {
    if (isCreate.value) {
      const createDto: PositionCreateDto = {
        AccountName: form.value.AccountName.trim(),
        Purpose: form.value.Purpose.trim() || undefined,
        IsActive: form.value.IsActive,
        TerminalPolicy: policyDiff(),
        GrantUserIds: stagedGrantUserIds.value.length > 0 ? stagedGrantUserIds.value : undefined,
        Terminals: stagedTerminals.value.length > 0
          ? stagedTerminals.value.map((slot) => ({
              DisplayName: slot.DisplayName,
              Location: slot.Location || undefined,
              WebAuthnRpId: slot.WebAuthnRpId,
            }))
          : undefined,
      }
      await store.createEntity(createDto)
    } else {
      // Send only fields that actually changed. Empty Purpose = explicit clear
      // (server normalises blank to null).
      const body: PositionUpdateDto = {
        Purpose: form.value.Purpose.trim() === '' ? null : form.value.Purpose.trim(),
      }
      if (form.value.AccountName.trim() !== original.value.AccountName) {
        body.AccountName = form.value.AccountName.trim()
      }
      if (form.value.IsActive !== original.value.IsActive) {
        body.IsActive = form.value.IsActive
      }
      const diff = policyDiff()
      if (diff) body.TerminalPolicy = diff
      await store.httpClient.addPath(props.id).put(body)
    }
    props.close()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    error.value = err?.data?.Message ?? err?.message ?? String(e)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <ModalLayout :close="close" :title="modalTitle" icon="briefcase" :footer-button="footerButton">
    <div v-if="!loading || isCreate" class="position-editor">
      <CoarTabGroup v-model="activeTab" class="tab-bar">
        <CoarTab id="general">
          <span class="tab-label">
            {{ t('admin.positions.tabs.general', {}, 'General') }}
            <CoarPopover v-if="generalIssues.length" mode="hover" :offset="8">
              <span class="tab-issue" role="img" :aria-label="generalIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
              <template #content>
                <div class="tab-issue-panel">
                  <h4>{{ t('admin.positions.validation.incomplete', {}, 'Missing information') }}</h4>
                  <ul>
                    <li v-for="issue in generalIssues" :key="issue">{{ issue }}</li>
                  </ul>
                </div>
              </template>
            </CoarPopover>
          </span>
        </CoarTab>
        <CoarTab id="terminals">
          <span class="tab-label">
            {{ t('admin.positions.tabs.terminals', {}, 'Terminals') }}
            <CoarPopover v-if="terminalIssues.length" mode="hover" :offset="8">
              <span class="tab-issue" role="img" :aria-label="terminalIssues.join(' ')">
                <CoarIcon name="circle-alert" size="s" />
              </span>
              <template #content>
                <div class="tab-issue-panel">
                  <h4>{{ t('admin.positions.validation.incomplete', {}, 'Missing information') }}</h4>
                  <ul>
                    <li v-for="issue in terminalIssues" :key="issue">{{ issue }}</li>
                  </ul>
                </div>
              </template>
            </CoarPopover>
          </span>
        </CoarTab>
        <CoarTab id="grants">{{ t('admin.positions.tabs.grants', {}, 'Authorized users') }}</CoarTab>
        <!-- Rule 5: absent in create — sessions cannot exist before the position. -->
        <CoarTab v-if="!isCreate" id="sessions">{{ t('admin.positions.tabs.sessions', {}, 'Staffing sessions') }}</CoarTab>
      </CoarTabGroup>

      <!-- Tab: General -->
      <div v-show="activeTab === 'general'" class="tab-content modal-form">
        <!-- Section: Basis -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.positions.section.basics', {}, 'Basics') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full" :label="t('admin.positions.accountName', {}, 'Account name')" required
              :error="accountNameError"
              :hint="t('admin.positions.accountNameHint', {}, 'Lowercase letters, digits, dots, hyphens or underscores. Becomes the token subject handle and audit identity of this position.')">
              <CoarTextInput v-model="form.AccountName" clearable :disabled="!isCreate"
                :placeholder="t('admin.positions.accountNamePlaceholder', {}, 'porter.customer-xy, reception.hq, …')" />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.positions.purpose', {}, 'Purpose')"
              :hint="t('admin.positions.purposeHint', {}, 'Free text describing what this position is for. Optional.')">
              <CoarTextInput v-model="form.Purpose" clearable
                :placeholder="t('admin.positions.purposePlaceholder', {}, 'Gate porter for customer XY, …')" />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Status -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.positions.section.status', {}, 'Status') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full"
              :label="t('admin.positions.active', {}, 'Active')"
              :hint="t('admin.positions.activeHint', {}, 'Deactivating immediately revokes every outstanding token of this position; staffing and enrollment stay blocked until reactivation.')"
              layout="inline"
              label-position="after">
              <CoarCheckbox v-model="form.IsActive" />
            </CoarFormField>
          </div>
        </section>
      </div>

      <!-- Tab: Terminals. Rule 1 — the lifetime fields stay VISIBLE when
           terminal use is off (disabled, showing the effective defaults);
           hiding them would make the policy unfindable. -->
      <div v-show="activeTab === 'terminals'" class="tab-content modal-form">
        <section class="form-section">
          <div class="modal-form-grid">
            <CoarFormField class="col-full"
              :label="t('admin.positions.terminalsEnabled', {}, 'Terminal use')"
              :hint="t('admin.positions.terminalsEnabledHint', {}, 'Off by default. Terminal slots can only be created and enrolled while this is on; staff then activate the position with a passkey tap.')"
              layout="inline"
              label-position="after">
              <CoarCheckbox v-model="form.TerminalEnabled" />
            </CoarFormField>
            <CoarFormField
              :label="t('admin.positions.sessionLifetime', {}, 'Staffing session (minutes)')"
              :error="lifetimeError"
              :hint="t('admin.positions.sessionLifetimeHint', {}, 'How long one staffing session lasts — typically a shift (960 = 16 hours). Access tokens stay short-lived independently of this.')">
              <CoarNumberInput v-model="form.StaffingSessionLifetimeMinutes" :min="1" :disabled="!form.TerminalEnabled" />
            </CoarFormField>
            <CoarFormField
              :label="t('admin.positions.maxSessionLifetime', {}, 'Absolute maximum (minutes)')"
              :hint="t('admin.positions.maxSessionLifetimeHint', {}, 'The hard ceiling a refresh can never extend past (1440 = 24 hours).')">
              <CoarNumberInput v-model="form.MaximumStaffingSessionLifetimeMinutes" :min="1" :disabled="!form.TerminalEnabled" />
            </CoarFormField>
          </div>

          <!-- Terminal slots (MG-FT-03). Rule 1: visible in every state — the
               create row is disabled (with the reason as hint) until the
               PERSISTED policy allows slots. Slot ops are immediate actions. -->
          <div class="mt-4">
            <div class="mb-3 flex flex-wrap items-end gap-2">
              <CoarFormField class="min-w-0 flex-1" :label="t('admin.positionTerminals.name', {}, 'Terminal name')">
                <CoarTextInput v-model="newTerminal.DisplayName" :disabled="!canAddTerminal"
                  :placeholder="t('admin.positionTerminals.namePlaceholder', {}, 'Gate terminal left, …')" />
              </CoarFormField>
              <CoarFormField class="min-w-0 flex-1" :label="t('admin.positionTerminals.location', {}, 'Location')">
                <CoarTextInput v-model="newTerminal.Location" :disabled="!canAddTerminal"
                  :placeholder="t('admin.positionTerminals.locationPlaceholder', {}, 'Gate 3, …')" />
              </CoarFormField>
              <CoarFormField class="min-w-0 flex-1" :label="t('admin.positionTerminals.rpId', {}, 'WebAuthn RP ID')"
                :hint="existingRpId
                  ? t('admin.positionTerminals.rpIdLockedHint', {}, 'Inherited from the existing slots: staff passkeys hang off the RP ID, so every slot of this position shares it — only then do the already-enrolled tokens unlock a new terminal.')
                  : t('admin.positionTerminals.rpIdHint', {}, 'The RP ID staff passkeys verify against — usually shared by every terminal of the consuming app.')">
                <CoarTextInput v-model="newTerminal.WebAuthnRpId" :disabled="!canAddTerminal || !!existingRpId"
                  placeholder="alerthub.example.com" />
              </CoarFormField>
              <CoarButton size="s" icon-start="plus" class="shrink-0 mb-1"
                :disabled="!canAddTerminal || !newTerminal.DisplayName.trim() || !newTerminal.WebAuthnRpId.trim()"
                @click="isCreate ? stageTerminal() : createTerminal()">
                {{ t('admin.positionTerminals.createButton', {}, 'Add slot') }}
              </CoarButton>
            </div>
            <CoarNotice v-if="!canAddTerminal" variant="info" class="mb-3">
              {{ isCreate
                ? t('admin.positionTerminals.enablePolicyStaged', {}, 'Turn terminal use on to add slots — they are created together with the position.')
                : t('admin.positionTerminals.enablePolicyFirst', {}, 'Enable terminal use and save before creating slots.') }}
            </CoarNotice>

            <!-- Create: the staged slots, committed by the single Save. -->
            <template v-if="isCreate">
              <div v-if="stagedTerminals.length === 0" class="grant-empty">
                {{ t('admin.positionTerminals.emptyStaged', {}, 'No terminal slots staged yet.') }}
              </div>
              <ul v-else class="flex flex-col gap-2">
                <li v-for="(slot, index) in stagedTerminals" :key="`${slot.DisplayName}-${index}`"
                    class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
                  <div class="flex min-w-0 flex-1 flex-col">
                    <span class="truncate font-medium">{{ slot.DisplayName }}</span>
                    <span class="truncate text-xs text-surface-500">
                      {{ slot.WebAuthnRpId }}
                      <template v-if="slot.Location"> · {{ slot.Location }}</template>
                    </span>
                  </div>
                  <CoarTag variant="info">{{ t('admin.positionTerminals.statusStaged', {}, 'On save') }}</CoarTag>
                  <CoarButton size="s" variant="ghost" icon-start="x" @click="unstageTerminal(index)">
                    {{ t('common.remove', {}, 'Remove') }}
                  </CoarButton>
                </li>
              </ul>
            </template>

            <template v-else>
              <div v-if="terminalsLoading" class="text-xs text-surface-500">
                {{ t('common.loading', {}, 'Loading...') }}
              </div>
              <div v-else-if="terminals.length === 0" class="grant-empty">
                {{ t('admin.positionTerminals.empty', {}, 'No terminal slots yet.') }}
              </div>
              <ul v-else class="flex flex-col gap-2">
                <li v-for="terminal in terminals" :key="terminal.Id"
                    class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
                  <div class="flex min-w-0 flex-1 flex-col">
                    <span class="truncate font-medium">{{ terminal.DisplayName }}</span>
                    <span class="truncate text-xs text-surface-500">
                      <code>{{ terminal.ClientId }}</code>
                      <template v-if="terminal.Location"> · {{ terminal.Location }}</template>
                    </span>
                  </div>
                  <CoarTag :variant="terminal.Status === 'Active' ? 'success'
                    : terminal.Status === 'Pending' ? 'info'
                    : terminal.Status === 'Disabled' ? 'warning' : 'neutral'">
                    {{ terminal.Status === 'Active' ? t('admin.positionTerminals.statusActive', {}, 'Active')
                      : terminal.Status === 'Pending' ? t('admin.positionTerminals.statusPending', {}, 'Pending enrollment')
                      : terminal.Status === 'Disabled' ? t('admin.positionTerminals.statusDisabled', {}, 'Disabled')
                      : t('admin.positionTerminals.statusRevoked', {}, 'Revoked') }}
                  </CoarTag>
                  <div v-if="terminal.Status !== 'Revoked'" class="flex items-center gap-1">
                    <CoarButton v-if="terminal.Status !== 'Disabled'" size="s" variant="ghost" icon-start="pause"
                      @click="transitionTerminal(terminal, 'disable')">
                      {{ t('admin.positionTerminals.disableButton', {}, 'Disable') }}
                    </CoarButton>
                    <CoarButton v-else size="s" variant="ghost" icon-start="play"
                      @click="transitionTerminal(terminal, 'reactivate')">
                      {{ t('admin.positionTerminals.reactivateButton', {}, 'Reactivate') }}
                    </CoarButton>
                    <CoarPopconfirm
                      :title="t('admin.positionTerminals.revokeTitle', {}, 'Revoke terminal?')"
                      :message="t('admin.positionTerminals.revokeConfirm', {}, 'Revoking is permanent: the device is cut off immediately and needs a brand-new slot (with a fresh enrollment) to ever return.')"
                      confirm-variant="danger"
                      @confirmed="transitionTerminal(terminal, 'revoke')">
                      <CoarButton size="s" variant="ghost" icon-start="trash-2">
                        {{ t('admin.positionTerminals.revokeButton', {}, 'Revoke') }}
                      </CoarButton>
                    </CoarPopconfirm>
                  </div>
                </li>
              </ul>
            </template>
          </div>
        </section>
      </div>

      <!-- Staffing sessions (MG-FT-05/07) — the live/audit view of shifts on
           this position's terminals; force-lock ends a running one remotely.
           Edit-mode only: sessions cannot exist before the position does. -->
      <section v-if="!isCreate" v-show="activeTab === 'sessions'" class="form-section tab-content">
        <div v-if="sessionsLoading" class="text-xs text-surface-500">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>
        <div v-else-if="staffingSessions.length === 0" class="grant-empty">
          {{ t('admin.staffingSessions.empty', {}, 'No staffing sessions yet.') }}
        </div>
        <ul v-else class="flex flex-col gap-2">
          <li v-for="s in staffingSessions" :key="s.Id"
              class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
            <div class="flex min-w-0 flex-1 flex-col">
              <span class="truncate font-medium">
                {{ terminalLabel(s.TerminalId) }}
                <span class="font-normal text-surface-500">· {{ userLabel(s.ActivatedByUserId) }}</span>
              </span>
              <span class="truncate text-xs text-surface-500">
                {{ formatTime(s.StartedAt) }}
                <template v-if="s.Status === 'Active'">
                  → {{ t('admin.staffingSessions.until', {}, 'until at most') }} {{ formatTime(s.AbsoluteExpiresAt) }}
                </template>
                <template v-else>
                  → {{ formatTime(s.EndedAt) }} · {{ endReasonLabel(s.EndReason) }}
                </template>
              </span>
            </div>
            <CoarTag :variant="s.Status === 'Active' ? 'success' : 'neutral'">
              {{ s.Status === 'Active'
                ? t('admin.staffingSessions.statusActive', {}, 'Active')
                : t('admin.staffingSessions.statusEnded', {}, 'Ended') }}
            </CoarTag>
            <CoarPopconfirm v-if="s.Status === 'Active'"
              :title="t('admin.staffingSessions.forceLockTitle', {}, 'Force-lock session?')"
              :message="t('admin.staffingSessions.forceLockConfirm', {}, 'The terminal is locked immediately and its tokens are revoked; staff must tap their passkey again to continue.')"
              confirm-variant="danger"
              @confirmed="forceLockSession(s.Id)">
              <CoarButton size="s" variant="ghost" icon-start="lock">
                {{ t('admin.staffingSessions.forceLockButton', {}, 'Force-lock') }}
              </CoarButton>
            </CoarPopconfirm>
          </li>
        </ul>
      </section>

      <!-- Rule 5: same section in both modes — create STAGES grants (the one
           Save commits position + grants atomically), edit operates on live
           grants immediately (rule 2: own lifecycle, explicit actions). -->
      <section v-show="activeTab === 'grants'" class="form-section tab-content">
        <div class="mb-3 flex items-center gap-2">
          <CoarSelect
            v-model="selectedGrantUserId"
            :options="grantableUserOptions"
            searchable
            class="min-w-0 flex-1"
            :placeholder="t('admin.positionGrants.pickUser', {}, 'Select a user…')" />
          <CoarButton size="s" icon-start="plus" class="shrink-0" :disabled="!selectedGrantUserId"
            @click="isCreate ? stageGrant() : issueGrant()">
            {{ t('admin.positionGrants.issueButton', {}, 'Grant') }}
          </CoarButton>
        </div>

        <template v-if="isCreate">
          <div v-if="stagedGrantUserIds.length === 0" class="grant-empty">
            {{ t('admin.positionGrants.stagedEmpty', {}, 'No users staged yet — they are authorized together with the create.') }}
          </div>
          <ul v-else class="flex flex-col gap-2">
            <li v-for="userId in stagedGrantUserIds" :key="userId"
                class="flex items-center gap-2 rounded border border-surface-200 p-3">
              <span class="min-w-0 flex-1 truncate font-medium">{{ userLabel(userId) }}</span>
              <CoarButton size="s" variant="ghost" icon-start="trash-2" @click="unstageGrant(userId)">
                {{ t('common.remove', {}, 'Remove') }}
              </CoarButton>
            </li>
          </ul>
        </template>

        <div v-else-if="grantsLoading" class="text-xs text-surface-500">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>
        <div v-else-if="grants.length === 0" class="grant-empty">
          {{ t('admin.positionGrants.empty', {}, 'No user is authorized to staff this position yet.') }}
        </div>
        <ul v-else class="flex flex-col gap-2">
          <li v-for="grant in grants" :key="grant.Id"
              class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
            <div class="flex min-w-0 flex-1 flex-col">
              <span class="truncate font-medium">{{ grant.UserDisplayName || grant.UserAccountName || grant.UserId }}</span>
              <span v-if="grant.UserAccountName" class="truncate text-xs text-surface-500">{{ grant.UserAccountName }}</span>
            </div>
            <CoarTag v-if="!grant.UserHasPasskey && grant.Status !== 'Revoked'" variant="warning">
              {{ t('admin.positionGrants.noPasskey', {}, 'No passkey') }}
            </CoarTag>
            <CoarTag :variant="grant.Status === 'Active' ? 'success' : grant.Status === 'Suspended' ? 'warning' : 'neutral'">
              {{ grant.Status === 'Active'
                ? t('admin.positionGrants.statusActive', {}, 'Active')
                : grant.Status === 'Suspended'
                  ? t('admin.positionGrants.statusSuspended', {}, 'Suspended')
                  : t('admin.positionGrants.statusRevoked', {}, 'Revoked') }}
            </CoarTag>
            <div v-if="grant.Status !== 'Revoked'" class="flex items-center gap-1">
              <CoarButton v-if="grant.Status === 'Active'" size="s" variant="ghost" icon-start="pause"
                @click="transitionGrant(grant, 'suspend')">
                {{ t('admin.positionGrants.suspendButton', {}, 'Suspend') }}
              </CoarButton>
              <CoarButton v-else size="s" variant="ghost" icon-start="play"
                @click="transitionGrant(grant, 'resume')">
                {{ t('admin.positionGrants.resumeButton', {}, 'Resume') }}
              </CoarButton>
              <CoarPopconfirm
                :title="t('admin.positionGrants.revokeTitle', {}, 'Revoke grant?')"
                :message="t('admin.positionGrants.revokeConfirm', {}, 'Revoking is permanent — re-authorizing this user later creates a new grant with a fresh audit trail.')"
                confirm-variant="danger"
                @confirmed="transitionGrant(grant, 'revoke')">
                <CoarButton size="s" variant="ghost" icon-start="trash-2">
                  {{ t('admin.positionGrants.revokeButton', {}, 'Revoke') }}
                </CoarButton>
              </CoarPopconfirm>
            </div>
          </li>
        </ul>
      </section>

      <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>
    </div>
    <div v-else class="flex flex-1 items-center justify-center p-8">
      <span class="text-gray-400">{{ t('common.loading', {}, 'Loading...') }}</span>
    </div>
  </ModalLayout>
</template>

<style scoped>
/* Pinned body height so the modal keeps ONE size across all tabs (no resize
   on tab switch) — same pattern as .user-edit-frame in UserDetails. Applies
   in create too, because create is tabbed as well. flex: 0 0 auto is required
   so the height wins inside the modal's flex column. */
.position-editor {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
  min-width: 0;
  min-height: 0;
  padding: 0.25rem;
  flex: 0 0 auto;
  height: 60vh;
}

.form-section + .form-section {
  margin-top: 1.5rem;
}

.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.grant-empty {
  padding: 1rem;
  border: 1px dashed var(--coar-border-neutral-secondary, #d1d5db);
  border-radius: 0.25rem;
  color: var(--coar-text-neutral-secondary, #6b7280);
  font-size: 0.875rem;
  text-align: center;
}

.tab-bar {
  margin-bottom: 12px;
}

.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 2px 2px 16px;
  min-height: 0;
  /* Long grant/slot/session lists scroll inside the pinned frame instead of
     growing the modal. */
  overflow-y: auto;
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

.tab-issue {
  display: flex;
  align-items: center;
  color: var(--coar-text-warning-primary, #b45309);
  cursor: help;
}

.tab-issue-panel {
  width: min(24rem, 70vw);
  padding: 0.75rem 0.875rem;
}

.tab-issue-panel h4 {
  margin: 0 0 0.4rem;
  font-size: 0.875rem;
  font-weight: 600;
}

.tab-issue-panel ul {
  margin: 0;
  padding-left: 1rem;
  font-size: 0.8rem;
}
</style>
