<script setup lang="ts">
/**
 * ADR 0026 — the test-account section of a user's security tab. It writes live, not
 * through a draft: the fixed code is a secret that never travels in a manifest, and it
 * needs the marker to be live first. Shown only to admins holding user:test-account.
 */
import { computed, onMounted, ref } from 'vue'
import { CoarButton, CoarCheckbox, CoarDivider, CoarFormField, CoarNotice, CoarNumberInput, CoarTag, CoarTextInput } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { HttpClientError } from '@/composables/useHttpClient'
import { useUserStore } from '@/stores/user.store'

const props = defineProps<{ userId: string }>()

const { t, language } = useI18n()
const userStore = useUserStore()

type Status = Awaited<ReturnType<typeof userStore.getTestAccount>>
const status = ref<Status | null>(null)
const busy = ref(false)
const error = ref('')
const code = ref('')
const validDays = ref<number | null>(null)
const codeSaved = ref(false)

const codeValid = computed(() => /^[0-9]{6}$/.test(code.value))

async function load() {
  status.value = await userStore.getTestAccount(props.userId)
}

onMounted(load)

function describe(e: unknown): string {
  if (e instanceof HttpClientError) {
    const body = e.body as { Message?: string } | undefined
    return body?.Message ?? e.statusText
  }
  return t('common.connectionError', {}, 'Connection to server failed.')
}

async function run(action: () => Promise<void>) {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await action()
    await load()
  } catch (e) {
    error.value = describe(e)
  } finally {
    busy.value = false
  }
}

function toggleMarker(next: boolean) {
  return run(async () => {
    await userStore.setTestAccount(props.userId, next)
    code.value = ''
    codeSaved.value = false
  })
}

function generate() {
  const digits = new Uint32Array(1)
  crypto.getRandomValues(digits)
  code.value = String(digits[0]! % 1_000_000).padStart(6, '0')
}

function saveCode() {
  if (!codeValid.value) return
  return run(async () => {
    const expiresAt = validDays.value && validDays.value > 0
      ? new Date(Date.now() + validDays.value * 24 * 60 * 60 * 1000).toISOString()
      : null
    await userStore.setFixedEmailCode(props.userId, code.value, expiresAt)
    codeSaved.value = true
  })
}

function removeCode() {
  return run(async () => {
    await userStore.removeFixedEmailCode(props.userId)
    code.value = ''
    codeSaved.value = false
  })
}

function formatDate(value: string | null): string {
  if (!value) return ''
  return new Date(value).toLocaleString(language.value === 'de' ? 'de-AT' : 'en-GB',
    { dateStyle: 'medium', timeStyle: 'short' })
}
</script>

<template>
  <div v-if="status">
    <CoarDivider align="left" variant="subtle" :width="100" :spacing-bottom="12">
      <h3 class="section-divider__title">{{ t('admin.userDetails.testAccount.heading', {}, 'Test account') }}</h3>
    </CoarDivider>

    <div class="flex flex-col gap-3">
      <CoarFormField
        :label="t('admin.userDetails.testAccount.marker', {}, 'This is a test account')"
        :hint="t('admin.userDetails.testAccount.markerHint', {}, 'An ordinary account that every token flags with modgud.test_account. It never holds Modgud\'s own administration and is kept out of groups that exclude test accounts. Takes effect immediately.')"
        layout="inline"
        label-position="after">
        <CoarCheckbox :model-value="status.IsTestAccount" :disabled="busy" @update:model-value="toggleMarker($event === true)" />
      </CoarFormField>

      <template v-if="status.IsTestAccount">
        <div class="flex items-center gap-2">
          <span class="text-gray-600">{{ t('admin.userDetails.testAccount.fixedCode', {}, 'Fixed e-mail code:') }}</span>
          <CoarTag v-if="status.HasFixedEmailCode" variant="success" size="s">
            {{ status.FixedEmailCodeExpiresAt
              ? t('admin.userDetails.testAccount.codeUntil', { date: formatDate(status.FixedEmailCodeExpiresAt) }, `Set, valid until ${formatDate(status.FixedEmailCodeExpiresAt)}`)
              : t('admin.userDetails.testAccount.codeSet', {}, 'Set, no expiry') }}
          </CoarTag>
          <CoarTag v-else variant="neutral" size="s">{{ t('admin.userDetails.testAccount.codeNone', {}, 'None — codes are sent by e-mail') }}</CoarTag>
        </div>
        <div v-if="status.FixedEmailCodeLastUsedAt" class="text-gray-600">
          {{ t('admin.userDetails.testAccount.lastUsed',
            { date: formatDate(status.FixedEmailCodeLastUsedAt), client: status.FixedEmailCodeLastUsedClientId ?? '—' },
            `Last used ${formatDate(status.FixedEmailCodeLastUsedAt)} (${status.FixedEmailCodeLastUsedClientId ?? '—'})`) }}
        </div>

        <CoarNotice variant="info">
          {{ t('admin.userDetails.testAccount.codeInfo', {}, 'With a fixed code the account receives no code mail: the e-mail-code sign-in accepts this code instead. Six digits, like a sent code. It is shown only now — write it down before saving.') }}
        </CoarNotice>

        <div class="test-account-code">
          <CoarFormField :label="t('admin.userDetails.testAccount.newCode', {}, 'New code')">
            <CoarTextInput v-model="code" placeholder="000000" :maxlength="6" inputmode="numeric" autocomplete="off" />
          </CoarFormField>
          <CoarButton size="s" variant="secondary" icon-start="shuffle" :disabled="busy" @click="generate">
            {{ t('admin.userDetails.testAccount.generate', {}, 'Generate') }}
          </CoarButton>
          <CoarFormField :label="t('admin.userDetails.testAccount.validDays', {}, 'Valid for (days)')"
            :hint="t('admin.userDetails.testAccount.validDaysHint', {}, 'Empty = no expiry.')">
            <CoarNumberInput v-model="validDays" :min="1" :step="1" />
          </CoarFormField>
        </div>
        <div class="flex gap-2">
          <CoarButton size="s" :disabled="!codeValid" :loading="busy" @click="saveCode">
            {{ t('admin.userDetails.testAccount.saveCode', {}, 'Set code') }}
          </CoarButton>
          <CoarButton v-if="status.HasFixedEmailCode" size="s" variant="danger" :loading="busy" @click="removeCode">
            {{ t('admin.userDetails.testAccount.removeCode', {}, 'Remove code') }}
          </CoarButton>
        </div>
        <CoarNotice v-if="codeSaved" variant="success">
          {{ t('admin.userDetails.testAccount.saved', {}, 'Code saved.') }}
        </CoarNotice>
      </template>

      <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>
    </div>
  </div>
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
