<script setup lang="ts">
import { ref, computed } from 'vue'
import { CoarButton, CoarIcon, useToast } from '@cocoar/vue-ui'
import { useI18n } from '@cocoar/vue-localization'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useAuthStore } from '@/stores/auth.store'

// Shows when the logged-in user has an email on file that the IdP has not
// flagged as verified. Sending a verification mail is a 1-click action —
// the endpoint resolves the recipient from the Identity cookie, so no
// typing is needed here. Dismissible per browser tab.

const { t } = useI18n()
const authStore = useAuthStore()
const toast = useToast()
const http = useHttpClient('/api/account/email')

const dismissed = ref(sessionStorage.getItem('unverified-email-banner-dismissed') === 'true')
const sending = ref(false)

const visible = computed(() => {
  const u = authStore.user
  return !!u && !!u.Email && u.EmailConfirmed === false && !dismissed.value
})

function dismiss() {
  dismissed.value = true
  sessionStorage.setItem('unverified-email-banner-dismissed', 'true')
}

async function sendVerification() {
  if (sending.value) return
  sending.value = true
  try {
    // Empty body — server reads the authenticated user via cookie.
    await http.addPath('send-verification').post({})
    toast.success(t('emailBanner.sent', {}, 'Verification email sent. Please check your inbox.'))
    dismiss()
  } catch (e: unknown) {
    const msg = e instanceof HttpClientError && e.status === 429
      ? t('emailBanner.rateLimit', {}, 'Too many requests. Please try again later.')
      : t('emailBanner.failed', {}, 'Could not send verification email.')
    toast.error(msg)
  } finally {
    sending.value = false
  }
}
</script>

<template>
  <div v-if="visible" class="email-banner">
    <CoarIcon name="circle-help" size="s" class="email-banner-icon" />
    <div class="email-banner-text">
      <strong>{{ t('emailBanner.title', {}, 'Email not verified') }}</strong>
      <span class="email-banner-detail">
        {{ t('emailBanner.body', { email: authStore.user?.Email ?? '' },
            `Verify ${authStore.user?.Email ?? ''}. While unverified, profile changes, Email-OTP, password reset and login links are blocked.`) }}
      </span>
    </div>
    <div class="email-banner-actions">
      <CoarButton size="s" variant="primary" :loading="sending" @click="sendVerification">
        {{ t('emailBanner.send', {}, 'Send verification email') }}
      </CoarButton>
      <CoarButton size="s" variant="ghost" @click="dismiss">
        {{ t('emailBanner.dismiss', {}, 'Later') }}
      </CoarButton>
    </div>
  </div>
</template>

<style scoped>
.email-banner {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 16px;
  background: #fef3c7;
  color: #78350f;
  border-bottom: 1px solid #fcd34d;
  font-size: 0.85rem;
}
.email-banner-icon {
  color: #b45309;
  flex-shrink: 0;
}
.email-banner-text {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
}
.email-banner-detail {
  color: #78350f;
  opacity: 0.85;
}
.email-banner-actions {
  display: flex;
  gap: 6px;
  flex-shrink: 0;
}
</style>
