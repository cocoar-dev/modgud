<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { useFunctionStore } from '@/stores/function.store'
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
  useToast,
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useUserStore } from '@/stores/user.store'
import type { FunctionCreateDto, FunctionUpdateDto, FunctionTerminalPolicyUpdateDto, FunctionGrantDto } from '@/models/function'

const { t } = useI18n()
const toast = useToast()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useFunctionStore()
const userStore = useUserStore()
const isCreate = computed(() => props.id === 'create')
const loading = ref(false)
const error = ref<string | null>(null)

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
      'admin.functions.accountNameInvalid',
      {},
      '2–64 characters; only lowercase letters, digits, dot, hyphen, and underscore.',
    )
  return ''
})

const lifetimeError = computed(() => {
  const session = form.value.StaffingSessionLifetimeMinutes
  const maximum = form.value.MaximumStaffingSessionLifetimeMinutes
  if (session <= 0 || maximum <= 0)
    return t('admin.functions.lifetimePositive', {}, 'Lifetimes must be positive.')
  if (session > maximum)
    return t('admin.functions.lifetimeCeiling', {}, 'The session lifetime must not exceed the absolute maximum.')
  return ''
})

const modalTitle = computed(() => {
  return isCreate.value
    ? t('admin.functions.createTitle', {}, 'Create function')
    : (form.value.AccountName || t('admin.functions.editTitle', {}, 'Function'))
})

const footerButton = computed(() => ({
  visible: true,
  text: isCreate.value ? t('common.create', {}, 'Create') : t('common.save', {}, 'Save'),
  disabled: !form.value.AccountName.trim() || !!accountNameError.value || !!lifetimeError.value || loading.value,
  onClick: save,
}))

// ── Activation grants (MG-FT-02) — edit-mode only. Operations, not staged
// edits (modal-contract rule 2): grants have their own lifecycle and audit
// identity, mirroring the SA credentials tab, so issue/suspend/resume/revoke
// act immediately with explicit buttons, apart from the primary Save.
const grants = ref<FunctionGrantDto[]>([])
const grantsLoading = ref(false)
const selectedGrantUserId = ref<string | null>(null)
const grantsHttp = computed(() => useHttpClient(`/api/function/${props.id}/grants`))

const grantableUserOptions = computed(() => {
  const liveGrantUserIds = new Set(grants.value.filter((g) => g.Status !== 'Revoked').map((g) => g.UserId))
  return userStore.entities
    .filter((u) => u.IsActive && !liveGrantUserIds.has(u.Id))
    .map((u) => ({
      value: u.Id,
      label: `${u.Firstname ?? ''} ${u.Lastname ?? ''}`.trim() || u.Email || u.Id,
    }))
})

async function loadGrants() {
  if (isCreate.value) return
  grantsLoading.value = true
  try {
    grants.value = await grantsHttp.value.get<FunctionGrantDto[]>()
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

async function transitionGrant(grant: FunctionGrantDto, action: 'suspend' | 'resume' | 'revoke') {
  try {
    await grantsHttp.value.addPath(grant.Id, action).post()
    await loadGrants()
  } catch (e: unknown) {
    const err = e as { data?: { Message?: string }; message?: string }
    toast.error(err?.data?.Message ?? err?.message ?? String(e))
  }
}

onMounted(async () => {
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
      // Grants + the user list for the picker load alongside — neither may
      // block the form fields from painting.
      void loadGrants()
      void userStore.initialize()
    } catch (e: unknown) {
      const err = e as { data?: { Message?: string }; message?: string }
      error.value = err?.data?.Message ?? err?.message ?? String(e)
    } finally {
      loading.value = false
    }
  }
})

function policyDiff(): FunctionTerminalPolicyUpdateDto | undefined {
  const diff: FunctionTerminalPolicyUpdateDto = {}
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
      const createDto: FunctionCreateDto = {
        AccountName: form.value.AccountName.trim(),
        Purpose: form.value.Purpose.trim() || undefined,
        IsActive: form.value.IsActive,
        TerminalPolicy: policyDiff(),
      }
      await store.createEntity(createDto)
    } else {
      // Send only fields that actually changed. Empty Purpose = explicit clear
      // (server normalises blank to null).
      const body: FunctionUpdateDto = {
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
    <div v-if="!loading || isCreate" class="function-editor">
      <div class="modal-form">
        <!-- Section: Basis -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.functions.section.basics', {}, 'Basics') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full" :label="t('admin.functions.accountName', {}, 'Account name')" required
              :error="accountNameError"
              :hint="t('admin.functions.accountNameHint', {}, 'Lowercase letters, digits, dots, hyphens or underscores. Becomes the token subject handle and audit identity of this function.')">
              <CoarTextInput v-model="form.AccountName" clearable :disabled="!isCreate"
                :placeholder="t('admin.functions.accountNamePlaceholder', {}, 'porter.customer-xy, reception.hq, …')" />
            </CoarFormField>
            <CoarFormField class="col-full" :label="t('admin.functions.purpose', {}, 'Purpose')"
              :hint="t('admin.functions.purposeHint', {}, 'Free text describing what this function is for. Optional.')">
              <CoarTextInput v-model="form.Purpose" clearable
                :placeholder="t('admin.functions.purposePlaceholder', {}, 'Gate porter for customer XY, …')" />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Status -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.functions.section.status', {}, 'Status') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full"
              :label="t('admin.functions.active', {}, 'Active')"
              :hint="t('admin.functions.activeHint', {}, 'Deactivating immediately revokes every outstanding token of this function; staffing and enrollment stay blocked until reactivation.')"
              layout="inline"
              label-position="after">
              <CoarCheckbox v-model="form.IsActive" />
            </CoarFormField>
          </div>
        </section>

        <!-- Section: Terminals. Rule 1 — the lifetime fields stay VISIBLE when
             terminal use is off (disabled, showing the effective defaults);
             hiding them would make the policy unfindable. -->
        <section class="form-section">
          <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
            <h3 class="section-divider__title">{{ t('admin.functions.section.terminals', {}, 'Shared terminals') }}</h3>
          </CoarDivider>
          <div class="modal-form-grid">
            <CoarFormField class="col-full"
              :label="t('admin.functions.terminalsEnabled', {}, 'Terminal use')"
              :hint="t('admin.functions.terminalsEnabledHint', {}, 'Off by default. Terminal slots can only be created and enrolled while this is on; staff then activate the function with a passkey tap.')"
              layout="inline"
              label-position="after">
              <CoarCheckbox v-model="form.TerminalEnabled" />
            </CoarFormField>
            <CoarFormField
              :label="t('admin.functions.sessionLifetime', {}, 'Staffing session (minutes)')"
              :error="lifetimeError"
              :hint="t('admin.functions.sessionLifetimeHint', {}, 'How long one staffing session lasts — typically a shift (960 = 16 hours). Access tokens stay short-lived independently of this.')">
              <CoarNumberInput v-model="form.StaffingSessionLifetimeMinutes" :min="1" :disabled="!form.TerminalEnabled" />
            </CoarFormField>
            <CoarFormField
              :label="t('admin.functions.maxSessionLifetime', {}, 'Absolute maximum (minutes)')"
              :hint="t('admin.functions.maxSessionLifetimeHint', {}, 'The hard ceiling a refresh can never extend past (1440 = 24 hours).')">
              <CoarNumberInput v-model="form.MaximumStaffingSessionLifetimeMinutes" :min="1" :disabled="!form.TerminalEnabled" />
            </CoarFormField>
          </div>
        </section>
      </div>

      <!-- Grants exist only after create (rule 5: a section that cannot exist
           yet is absent). Actions are immediate operations, not staged edits. -->
      <section v-if="!isCreate" class="form-section">
        <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
          <h3 class="section-divider__title">{{ t('admin.functionGrants.sectionTitle', {}, 'Authorized users') }}</h3>
        </CoarDivider>

        <div class="mb-3 flex items-center gap-2">
          <CoarSelect
            v-model="selectedGrantUserId"
            :options="grantableUserOptions"
            searchable
            class="min-w-0 flex-1"
            :placeholder="t('admin.functionGrants.pickUser', {}, 'Select a user…')" />
          <CoarButton size="s" icon-start="plus" class="shrink-0" :disabled="!selectedGrantUserId" @click="issueGrant">
            {{ t('admin.functionGrants.issueButton', {}, 'Grant') }}
          </CoarButton>
        </div>

        <div v-if="grantsLoading" class="text-xs text-surface-500">
          {{ t('common.loading', {}, 'Loading...') }}
        </div>
        <div v-else-if="grants.length === 0" class="grant-empty">
          {{ t('admin.functionGrants.empty', {}, 'No user is authorized to staff this function yet.') }}
        </div>
        <ul v-else class="flex flex-col gap-2">
          <li v-for="grant in grants" :key="grant.Id"
              class="flex flex-wrap items-center gap-2 rounded border border-surface-200 p-3">
            <div class="flex min-w-0 flex-1 flex-col">
              <span class="truncate font-medium">{{ grant.UserDisplayName || grant.UserAccountName || grant.UserId }}</span>
              <span v-if="grant.UserAccountName" class="truncate text-xs text-surface-500">{{ grant.UserAccountName }}</span>
            </div>
            <CoarTag v-if="!grant.UserHasPasskey && grant.Status !== 'Revoked'" variant="warning">
              {{ t('admin.functionGrants.noPasskey', {}, 'No passkey') }}
            </CoarTag>
            <CoarTag :variant="grant.Status === 'Active' ? 'success' : grant.Status === 'Suspended' ? 'warning' : 'neutral'">
              {{ grant.Status === 'Active'
                ? t('admin.functionGrants.statusActive', {}, 'Active')
                : grant.Status === 'Suspended'
                  ? t('admin.functionGrants.statusSuspended', {}, 'Suspended')
                  : t('admin.functionGrants.statusRevoked', {}, 'Revoked') }}
            </CoarTag>
            <div v-if="grant.Status !== 'Revoked'" class="flex items-center gap-1">
              <CoarButton v-if="grant.Status === 'Active'" size="s" variant="ghost" icon-start="pause"
                @click="transitionGrant(grant, 'suspend')">
                {{ t('admin.functionGrants.suspendButton', {}, 'Suspend') }}
              </CoarButton>
              <CoarButton v-else size="s" variant="ghost" icon-start="play"
                @click="transitionGrant(grant, 'resume')">
                {{ t('admin.functionGrants.resumeButton', {}, 'Resume') }}
              </CoarButton>
              <CoarPopconfirm
                :title="t('admin.functionGrants.revokeTitle', {}, 'Revoke grant?')"
                :message="t('admin.functionGrants.revokeConfirm', {}, 'Revoking is permanent — re-authorizing this user later creates a new grant with a fresh audit trail.')"
                confirm-variant="danger"
                @confirmed="transitionGrant(grant, 'revoke')">
                <CoarButton size="s" variant="ghost" icon-start="trash-2">
                  {{ t('admin.functionGrants.revokeButton', {}, 'Revoke') }}
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
.function-editor {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
  min-width: 0;
  padding: 0.25rem;
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
</style>
