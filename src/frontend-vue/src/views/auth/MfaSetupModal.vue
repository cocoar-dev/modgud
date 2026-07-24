<script setup lang="ts">
import { computed, ref, onMounted } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import ModalLayout from '@/components/ModalLayout.vue'
import {
  CoarOtpInput,
  CoarFormField,
} from '@cocoar/vue-ui'
import AppNote from '@/components/AppNote.vue'

const { t } = useI18n()

// Opened as the routed `#mfa-setup` fragment on /profile — `close` is
// injected by the overlay host. Closing without a verified code resolves
// to `false`; the profile view re-reads the MFA status either way.
const props = defineProps<{
  close: (enabled?: boolean) => void
}>()

const http = useHttpClient('/api/account/mfa')

const sharedKey = ref('')
const authenticatorUri = ref('')
const verificationCode = ref('')
const error = ref('')
const submitting = ref(false)
const loading = ref(true)

onMounted(async () => {
  try {
    const result = await http.addPath('setup').post<{ SharedKey: string; AuthenticatorUri: string }>()
    sharedKey.value = result.SharedKey
    authenticatorUri.value = result.AuthenticatorUri
  } catch {
    error.value = t('profile.mfaSetup.setupFailed', {}, 'Setup failed.')
  } finally {
    loading.value = false
  }
})

const footerButton = computed(() => ({
  visible: !loading.value,
  text: t('profile.mfaSetup.confirm', {}, 'Confirm code and activate'),
  disabled: !verificationCode.value.trim(),
  loading: submitting.value,
  onClick: verifyCode,
}))

async function verifyCode() {
  if (!verificationCode.value.trim() || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    await http.addPath('verify').post({ Code: verificationCode.value.replace(/[\s-]/g, '') })
    props.close(true)
  } catch {
    error.value = t('profile.mfaSetup.invalidCode', {}, 'Invalid code. Please try again.')
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <ModalLayout
    :close="() => close(false)"
    :title="t('profile.mfaSetup.title', {}, 'Set up MFA')"
    icon="shield-check"
    :footer-button="footerButton"
  >
    <div>
      <div v-if="loading" class="text-center text-surface-400 py-8">{{ t('common.loading', {}, 'Loading...') }}</div>

      <template v-else>
        <p class="text-sm text-surface-600 mb-4">
          {{ t('profile.mfaSetup.instructions', {}, 'Scan the QR code with your authenticator app or enter the key manually.') }}
        </p>

        <!-- QR Code -->
        <div class="flex justify-center py-4">
          <img
            :src="`https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=${encodeURIComponent(authenticatorUri)}`"
            alt="QR Code"
            width="200"
            height="200"
            class="rounded border"
          />
        </div>

        <!-- Shared Key -->
        <div class="rounded bg-surface-100 p-3 text-center font-mono text-sm tracking-widest select-all mb-6">
          {{ sharedKey }}
        </div>

        <!-- Verification — committed by the modal footer button. -->
        <div class="space-y-4">
          <CoarFormField :label="t('profile.mfaSetup.verificationCode', {}, 'Verification Code')">
            <CoarOtpInput
              v-model="verificationCode"
              type="numeric"
              :length="6"
              auto-focus
              required
              @complete="verifyCode"
            />
          </CoarFormField>

          <AppNote v-if="error" variant="error" :truncate="false">{{ error }}</AppNote>
        </div>
      </template>
    </div>
  </ModalLayout>
</template>
