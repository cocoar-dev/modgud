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
} from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import type { FunctionCreateDto, FunctionUpdateDto, FunctionTerminalPolicyUpdateDto } from '@/models/function'

const { t } = useI18n()

const props = defineProps<{
  id: string
  close: (result?: unknown) => void
}>()

const store = useFunctionStore()
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
</style>
