<script setup lang="ts">
/**
 * ADR 0026 — the "Test account" tab of the user dialog. Pure form: the dialog's Save
 * commits it with everything else — onto the draft for a realm admin (the code goes
 * into the draft's encrypted secret store, like an initial password), live otherwise.
 */
import { computed } from 'vue'
import { CoarButton, CoarCheckbox, CoarDivider, CoarFormField, CoarNotice, CoarNumberInput, CoarTag, CoarTextInput } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'

export interface TestAccountLiveStatus {
  HasFixedEmailCode: boolean
  FixedEmailCodeExpiresAt: string | null
  FixedEmailCodeLastUsedAt: string | null
  FixedEmailCodeLastUsedClientId: string | null
}

defineProps<{
  /** The applied state; null for a user that only exists in the draft. */
  live: TestAccountLiveStatus | null
  /** A code is already staged in the draft's secret store. */
  stagedCode: boolean
  stagedExpiresAt: string | null
  staging: boolean
}>()

const code = defineModel<string>('code', { required: true })
const validDays = defineModel<number | null>('validDays', { required: true })
const removeCode = defineModel<boolean>('removeCode', { required: true })

const { t, language } = useI18n()

const codeInvalid = computed(() => code.value !== '' && !/^[0-9]{6}$/.test(code.value))

function generate() {
  const digits = new Uint32Array(1)
  crypto.getRandomValues(digits)
  code.value = String(digits[0]! % 1_000_000).padStart(6, '0')
  removeCode.value = false
}

function formatDate(value: string | null): string {
  if (!value) return ''
  return new Date(value).toLocaleString(language.value === 'de' ? 'de-AT' : 'en-GB',
    { dateStyle: 'medium', timeStyle: 'short' })
}
</script>

<template>
  <section class="flex flex-col gap-4 text-sm">
    <div>
      <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
        <h3 class="section-divider__title">{{ t('admin.userDetails.testAccount.fixedCodeHeading', {}, 'Fixed e-mail code') }}</h3>
      </CoarDivider>

      <div class="flex flex-col gap-2">
        <div class="flex items-center gap-2 flex-wrap">
          <span class="text-gray-600">{{ t('admin.userDetails.testAccount.applied', {}, 'Applied:') }}</span>
          <CoarTag v-if="live?.HasFixedEmailCode" variant="success" size="s">
            {{ live.FixedEmailCodeExpiresAt
              ? t('admin.userDetails.testAccount.codeUntil', { date: formatDate(live.FixedEmailCodeExpiresAt) }, `Set, valid until ${formatDate(live.FixedEmailCodeExpiresAt)}`)
              : t('admin.userDetails.testAccount.codeSet', {}, 'Set, no expiry') }}
          </CoarTag>
          <CoarTag v-else variant="neutral" size="s">{{ t('admin.userDetails.testAccount.codeNone', {}, 'None — codes are sent by e-mail') }}</CoarTag>
        </div>
        <div v-if="staging && stagedCode" class="flex items-center gap-2 flex-wrap">
          <span class="text-gray-600">{{ t('admin.userDetails.testAccount.inDraft', {}, 'In the draft:') }}</span>
          <CoarTag variant="warning" size="s">
            {{ stagedExpiresAt
              ? t('admin.userDetails.testAccount.stagedUntil', { date: formatDate(stagedExpiresAt) }, `New code staged, valid until ${formatDate(stagedExpiresAt)}`)
              : t('admin.userDetails.testAccount.staged', {}, 'New code staged, no expiry') }}
          </CoarTag>
        </div>
        <div v-if="live?.FixedEmailCodeLastUsedAt" class="text-gray-600">
          {{ t('admin.userDetails.testAccount.lastUsed',
            { date: formatDate(live.FixedEmailCodeLastUsedAt), client: live.FixedEmailCodeLastUsedClientId ?? '—' },
            `Last used ${formatDate(live.FixedEmailCodeLastUsedAt)} (${live.FixedEmailCodeLastUsedClientId ?? '—'})`) }}
        </div>
      </div>
    </div>

    <CoarNotice variant="info">
      {{ staging
        ? t('admin.userDetails.testAccount.codeInfoStaged', {}, 'With a fixed code the account receives no code mail: the e-mail-code sign-in accepts this code instead. Six digits, like a sent code. It is stored encrypted in the draft, never shown again and never exported — write it down before saving. It takes effect when the draft is applied.')
        : t('admin.userDetails.testAccount.codeInfo', {}, 'With a fixed code the account receives no code mail: the e-mail-code sign-in accepts this code instead. Six digits, like a sent code. It is shown only now — write it down before saving.') }}
    </CoarNotice>

    <div class="test-account-code">
      <CoarFormField :label="t('admin.userDetails.testAccount.newCode', {}, 'New code')"
        :error="codeInvalid ? t('admin.userDetails.testAccount.codeFormat', {}, 'Exactly six digits.') : undefined">
        <CoarTextInput v-model="code" placeholder="000000" :maxlength="6" inputmode="numeric" autocomplete="off" />
      </CoarFormField>
      <CoarButton size="s" variant="secondary" @click="generate">
        {{ t('admin.userDetails.testAccount.generate', {}, 'Generate') }}
      </CoarButton>
      <CoarFormField :label="t('admin.userDetails.testAccount.validDays', {}, 'Valid for (days)')"
        :hint="t('admin.userDetails.testAccount.validDaysHint', {}, 'Empty = no expiry.')">
        <CoarNumberInput v-model="validDays" :min="1" :step="1" />
      </CoarFormField>
    </div>

    <CoarFormField v-if="(live?.HasFixedEmailCode || stagedCode) && !code"
      :label="t('admin.userDetails.testAccount.removeCode', {}, 'Remove the fixed code')"
      :hint="t('admin.userDetails.testAccount.removeCodeHint', {}, 'Sign-in falls back to a code sent by e-mail.')"
      layout="inline"
      label-position="after">
      <CoarCheckbox v-model="removeCode" />
    </CoarFormField>
  </section>
</template>

<style scoped>
.section-divider__title {
  margin: 0;
  color: var(--coar-text-neutral-primary, #1f2937);
  font-size: 0.875rem;
  font-weight: 600;
}

.test-account-code {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 0.75rem;
}
</style>
