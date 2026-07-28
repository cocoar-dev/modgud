<script setup lang="ts">
import { ref, computed } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useI18n } from '@cocoar/vue-localization'
import { CoarPasswordInput, CoarFormField } from '@cocoar/vue-ui'
import ModalLayout from '@/components/ModalLayout.vue'
import Notice from '@/components/Notice.vue'

const { t } = useI18n()

// Opened as the routed `#change-password` fragment on /profile — `close` is
// injected by the overlay host.
const props = defineProps<{
  close: (result?: unknown) => void
}>()

const http = useHttpClient('/api/account')
const currentPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const saving = ref(false)
const error = ref('')
const success = ref(false)

const mismatch = computed(() =>
  confirmPassword.value.length > 0 && newPassword.value !== confirmPassword.value
)

const canSubmit = computed(() =>
  currentPassword.value.trim().length > 0 &&
  newPassword.value.trim().length >= 8 &&
  newPassword.value === confirmPassword.value &&
  !saving.value
)

const footerButton = computed(() => ({
  visible: true,
  text: t('profile.changePassword.button', {}, 'Change Password'),
  disabled: !canSubmit.value,
  loading: saving.value,
  onClick: changePassword,
}))

async function changePassword() {
  if (!canSubmit.value) return
  saving.value = true
  error.value = ''
  try {
    await http.addPath('change-password').post({
      CurrentPassword: currentPassword.value,
      NewPassword: newPassword.value,
    })
    success.value = true
    setTimeout(() => props.close(), 1500)
  } catch (e: any) {
    error.value = e?.data?.Message || t('profile.changePassword.failed', {}, 'Password change failed.')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <ModalLayout
    :close="close"
    :title="t('profile.changePassword.title', {}, 'Change Password')"
    icon="key"
    :footer-button="footerButton"
  >
    <div class="flex flex-col gap-4">
      <Notice v-if="success" variant="success">
        {{ t('profile.changePassword.success', {}, 'Password has been changed.') }}
      </Notice>

      <template v-else>
        <CoarFormField :label="t('profile.changePassword.currentPassword', {}, 'Current Password')">
          <CoarPasswordInput v-model="currentPassword" autocomplete="current-password" />
        </CoarFormField>

        <CoarFormField :label="t('profile.changePassword.newPassword', {}, 'New Password')">
          <CoarPasswordInput v-model="newPassword" autocomplete="new-password" />
        </CoarFormField>

        <CoarFormField :label="t('profile.changePassword.confirmPassword', {}, 'Confirm Password')">
          <CoarPasswordInput v-model="confirmPassword" autocomplete="new-password" />
        </CoarFormField>

        <Notice v-if="mismatch" variant="error">
          {{ t('profile.changePassword.mismatch', {}, 'Passwords do not match.') }}
        </Notice>

        <Notice v-if="error" variant="error">{{ error }}</Notice>
      </template>
    </div>
  </ModalLayout>
</template>
