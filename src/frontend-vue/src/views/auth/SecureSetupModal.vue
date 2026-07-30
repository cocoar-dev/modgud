<script setup lang="ts">
import { computed, ref } from 'vue'
import { useHttpClient } from '@/composables/useHttpClient'
import { useAuthStore } from '@/stores/auth.store'
import { useI18n } from '@cocoar/vue-localization'
import {
  CoarNotice,
  CoarCard,
  CoarButton,
  CoarOtpInput,
  CoarFormField,
  CoarIcon,
} from '@cocoar/vue-ui'

const props = defineProps<{
  /** Show "Später" button and countdown — user is still inside the grace period. */
  inGrace?: boolean
  /** UTC ISO timestamp when the grace period ends. */
  dueAt?: string | null
}>()

const emit = defineEmits<{
  complete: []
  postpone: []
  logout: []
}>()

const daysRemaining = computed(() => {
  if (!props.dueAt) return null
  const ms = new Date(props.dueAt).getTime() - Date.now()
  if (ms <= 0) return 0
  return Math.max(1, Math.ceil(ms / (1000 * 60 * 60 * 24)))
})

const { t } = useI18n()
const authStore = useAuthStore()
const mfaHttp = useHttpClient('/api/account/mfa')
const emailOtpHttp = useHttpClient('/api/account/email-otp')
const passkeyHttp = useHttpClient('/api/account/passkey')

// Which method is being set up (null = showing cards)
const activeSetup = ref<'totp' | 'email-otp' | 'passkey' | null>(null)
const error = ref('')
const submitting = ref(false)
const setupComplete = ref(false)

// ── TOTP Setup ──
const totpSharedKey = ref('')
const totpAuthUri = ref('')
const totpCode = ref('')
const totpLoading = ref(false)

async function startTotpSetup() {
  activeSetup.value = 'totp'
  totpLoading.value = true
  error.value = ''
  try {
    const result = await mfaHttp.addPath('setup').post<{ SharedKey: string; AuthenticatorUri: string }>()
    totpSharedKey.value = result.SharedKey
    totpAuthUri.value = result.AuthenticatorUri
  } catch {
    error.value = t('auth.secureSetup.setupFailed', {}, 'Setup failed. Please try again.')
  } finally {
    totpLoading.value = false
  }
}

async function verifyTotp() {
  if (!totpCode.value.trim() || submitting.value) return
  submitting.value = true
  error.value = ''
  try {
    await mfaHttp.addPath('verify').post({ Code: totpCode.value.replace(/[\s-]/g, '') })
    setupComplete.value = true
  } catch {
    error.value = t('auth.secureSetup.invalidCode', {}, 'Invalid code. Please try again.')
  } finally {
    submitting.value = false
  }
}

// ── Email OTP Setup ──
const emailOtpEnabling = ref(false)

async function enableEmailOtp() {
  emailOtpEnabling.value = true
  error.value = ''
  try {
    await emailOtpHttp.addPath('enable').post({})
    setupComplete.value = true
  } catch (e) {
    error.value = t('auth.secureSetup.setupFailed', {}, 'Setup failed. Please try again.')
  } finally {
    emailOtpEnabling.value = false
  }
}

// ── Passkey Setup ──
const passkeyRegistering = ref(false)

async function registerPasskey() {
  passkeyRegistering.value = true
  error.value = ''
  try {
    const serverOptions = await passkeyHttp.addPath('register-options').post<any>()
    const publicKey: PublicKeyCredentialCreationOptions = {
      rp: serverOptions.rp,
      user: { ...serverOptions.user, id: base64UrlToBuffer(serverOptions.user.id) },
      challenge: base64UrlToBuffer(serverOptions.challenge),
      pubKeyCredParams: serverOptions.pubKeyCredParams,
      timeout: serverOptions.timeout,
      attestation: serverOptions.attestation,
      authenticatorSelection: serverOptions.authenticatorSelection,
      excludeCredentials: (serverOptions.excludeCredentials ?? []).map((c: any) => ({
        ...c, id: base64UrlToBuffer(c.id),
      })),
    }
    const credential = await navigator.credentials.create({ publicKey }) as PublicKeyCredential
    if (!credential) throw new Error('Credential creation cancelled')
    const response = credential.response as AuthenticatorAttestationResponse
    await passkeyHttp.addPath('register').post({
      id: credential.id, rawId: bufferToBase64Url(credential.rawId), type: credential.type,
      response: { attestationObject: bufferToBase64Url(response.attestationObject), clientDataJSON: bufferToBase64Url(response.clientDataJSON) },
    })
    setupComplete.value = true
  } catch (e: any) {
    if (e.name !== 'NotAllowedError')
      error.value = e.message || t('auth.secureSetup.setupFailed', {}, 'Setup failed. Please try again.')
  } finally {
    passkeyRegistering.value = false
  }
}

function base64UrlToBuffer(b: string): ArrayBuffer {
  const s = atob(b.replace(/-/g, '+').replace(/_/g, '/').padEnd(b.length + (4 - b.length % 4) % 4, '='))
  const a = new Uint8Array(s.length); for (let i = 0; i < s.length; i++) a[i] = s.charCodeAt(i); return a.buffer
}
function bufferToBase64Url(b: ArrayBuffer): string {
  let s = ''; for (const x of new Uint8Array(b)) s += String.fromCharCode(x)
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}
</script>

<template>
  <div class="space-y-4">
    <!-- Success: method was set up -->
    <CoarCard v-if="setupComplete" elevated>
      <div class="p-6 text-center space-y-4">
        <div class="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-green-100">
          <CoarIcon name="check" class="text-green-600" size="l" />
        </div>
        <h2 class="text-lg font-semibold">{{ t('auth.secureSetup.success', {}, 'Account secured!') }}</h2>
        <p class="text-sm text-surface-500">{{ t('auth.secureSetup.successDescription', {}, 'You can add more methods later in your profile settings.') }}</p>
        <CoarButton full-width @click="emit('complete')">
          {{ t('auth.secureSetup.continue', {}, 'Continue') }}
        </CoarButton>
      </div>
    </CoarCard>

    <!-- TOTP Setup -->
    <CoarCard v-else-if="activeSetup === 'totp'" elevated>
      <div class="p-6 space-y-4">
        <h2 class="text-lg font-semibold">{{ t('auth.secureSetup.totpTitle', {}, 'Authenticator App') }}</h2>

        <div v-if="totpLoading" class="text-center text-sm text-surface-500">{{ t('common.loading', {}, 'Loading...') }}</div>
        <template v-else>
          <p class="text-sm text-surface-600">{{ t('auth.secureSetup.totpScanQr', {}, 'Scan this QR code with your authenticator app (Google Authenticator, Authy, etc.).') }}</p>

          <div class="flex justify-center">
            <img :src="`https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=${encodeURIComponent(totpAuthUri)}`" alt="QR Code" class="rounded" />
          </div>

          <details class="text-xs text-surface-500">
            <summary class="cursor-pointer hover:text-surface-700">{{ t('auth.secureSetup.totpManualKey', {}, 'Manual key') }}</summary>
            <code class="mt-1 block break-all bg-surface-100 p-2 rounded text-xs">{{ totpSharedKey }}</code>
          </details>

          <CoarFormField :label="t('auth.secureSetup.totpVerifyLabel', {}, 'Verification Code')">
            <CoarOtpInput v-model="totpCode" type="numeric" :length="6" auto-focus @complete="verifyTotp" />
          </CoarFormField>

          <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

          <CoarButton :disabled="!totpCode.trim()" :loading="submitting" full-width @click="verifyTotp">
            {{ t('auth.secureSetup.verify', {}, 'Verify & activate') }}
          </CoarButton>
        </template>

        <button type="button" class="block w-full text-center text-sm text-surface-500 hover:text-surface-700 hover:underline"
          @click="activeSetup = null; error = ''">
          {{ t('common.back', {}, 'Back') }}
        </button>
      </div>
    </CoarCard>

    <!-- Email OTP Setup -->
    <CoarCard v-else-if="activeSetup === 'email-otp'" elevated>
      <div class="p-6 space-y-4">
        <h2 class="text-lg font-semibold">{{ t('auth.secureSetup.emailOtpTitle', {}, 'Email Code') }}</h2>
        <p class="text-sm text-surface-600">
          {{ t('auth.secureSetup.emailOtpDescription', {}, 'A one-time code will be sent to your email address each time you log in.') }}
        </p>
        <p v-if="authStore.user?.Email" class="text-sm font-medium">{{ authStore.user.Email }}</p>
        <p v-else class="text-sm text-red-600">{{ t('auth.secureSetup.noEmail', {}, 'No email address configured. Contact your administrator.') }}</p>

        <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

        <CoarButton :disabled="!authStore.user?.Email" :loading="emailOtpEnabling" full-width @click="enableEmailOtp">
          {{ t('auth.secureSetup.enableEmailOtp', {}, 'Activate email code') }}
        </CoarButton>

        <button type="button" class="block w-full text-center text-sm text-surface-500 hover:text-surface-700 hover:underline"
          @click="activeSetup = null; error = ''">
          {{ t('common.back', {}, 'Back') }}
        </button>
      </div>
    </CoarCard>

    <!-- Passkey Setup -->
    <CoarCard v-else-if="activeSetup === 'passkey'" elevated>
      <div class="p-6 space-y-4">
        <h2 class="text-lg font-semibold">{{ t('auth.secureSetup.passkeyTitle', {}, 'Passkey') }}</h2>
        <p class="text-sm text-surface-600">
          {{ t('auth.secureSetup.passkeyDescription', {}, 'Use your fingerprint, face, or security key to sign in without a password.') }}
        </p>

        <CoarNotice v-if="error" variant="error">{{ error }}</CoarNotice>

        <CoarButton :loading="passkeyRegistering" full-width @click="registerPasskey">
          {{ t('auth.secureSetup.registerPasskey', {}, 'Register Passkey') }}
        </CoarButton>

        <button type="button" class="block w-full text-center text-sm text-surface-500 hover:text-surface-700 hover:underline"
          @click="activeSetup = null; error = ''">
          {{ t('common.back', {}, 'Back') }}
        </button>
      </div>
    </CoarCard>

    <!-- Method choice (3 cards) -->
    <template v-else>
      <CoarNotice v-if="inGrace && daysRemaining !== null" variant="warning">
        {{ t('auth.secureSetup.graceWarning', { days: daysRemaining }, `You have ${daysRemaining} day(s) left to secure your account.`) }}
      </CoarNotice>

      <CoarCard elevated class="cursor-pointer hover:ring-2 hover:ring-blue-300 transition" @click="startTotpSetup">
        <div class="p-4 flex items-center gap-4">
          <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-blue-100 text-blue-600 flex-shrink-0">
            <CoarIcon name="smartphone" size="m" />
          </div>
          <div>
            <div class="font-medium">{{ t('auth.secureSetup.totpCard', {}, 'Authenticator App') }}</div>
            <div class="text-xs text-surface-500">{{ t('auth.secureSetup.totpCardDescription', {}, 'Google Authenticator, Authy, etc.') }}</div>
          </div>
        </div>
      </CoarCard>

      <CoarCard elevated class="cursor-pointer hover:ring-2 hover:ring-blue-300 transition" @click="activeSetup = 'email-otp'">
        <div class="p-4 flex items-center gap-4">
          <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-green-100 text-green-600 flex-shrink-0">
            <CoarIcon name="mail" size="m" />
          </div>
          <div>
            <div class="font-medium">{{ t('auth.secureSetup.emailOtpCard', {}, 'Email Code') }}</div>
            <div class="text-xs text-surface-500">{{ t('auth.secureSetup.emailOtpCardDescription', {}, 'One-time code sent to your email.') }}</div>
          </div>
        </div>
      </CoarCard>

      <CoarCard elevated class="cursor-pointer hover:ring-2 hover:ring-blue-300 transition" @click="activeSetup = 'passkey'">
        <div class="p-4 flex items-center gap-4">
          <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-purple-100 text-purple-600 flex-shrink-0">
            <CoarIcon name="fingerprint" size="m" />
          </div>
          <div>
            <div class="font-medium">{{ t('auth.secureSetup.passkeyCard', {}, 'Passkey') }}</div>
            <div class="text-xs text-surface-500">{{ t('auth.secureSetup.passkeyCardDescription', {}, 'Fingerprint, Face ID, or security key.') }}</div>
          </div>
        </div>
      </CoarCard>

      <div class="flex flex-col gap-2 pt-1">
        <button v-if="inGrace" type="button"
          class="block w-full text-center text-sm text-surface-600 hover:text-surface-900 hover:underline"
          @click="emit('postpone')">
          {{ t('auth.secureSetup.postpone', {}, 'Postpone') }}
        </button>
        <button type="button"
          class="block w-full text-center text-sm text-surface-500 hover:text-surface-700 hover:underline"
          @click="emit('logout')">
          {{ t('nav.logout', {}, 'Logout') }}
        </button>
      </div>
    </template>
  </div>
</template>
