<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarTextInput,
  CoarPasswordInput,
  CoarFormField,
  CoarCheckbox,
  CoarNote,
} from '@cocoar/vue-ui'
import SecureSetupModal from './SecureSetupModal.vue'

const { t, language } = useI18n()
const localization = useLocalization()!
const appConfig = useAppConfigStore()

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const router = useRouter()
const route = useRoute()
const authStore = useAuthStore()

// Redirect target after login (from query param set by router guard)
const redirectTarget = computed(() => {
  const r = route.query.redirect as string | undefined
  return r ? decodeURIComponent(r) : '/'
})

// Paths outside the SPA (served directly by the backend) — Vue Router can't navigate
// there, so we use a full-page load after successful login.
const NON_SPA_PREFIXES = ['/docs/', '/docs']

function finishLogin() {
  const target = redirectTarget.value
  if (NON_SPA_PREFIXES.some((p) => target === p || target.startsWith(p + '/') || target.startsWith(p + '?'))) {
    window.location.assign(target)
  } else {
    router.push(target)
  }
}

// Form state
const userName = ref('')
const password = ref('')
const rememberMe = ref(false)
const totpCode = ref('')
const emailOtpCode = ref('')
const submitting = ref(false)
const error = ref('')

// External auth (OIDC) — fetched anonymously, filtered to enabled providers
interface ExternalLoginDto { Id: string; DisplayName: string; Flavor: string; IconName?: string | null; ButtonColorHex?: string | null }
const externalLogins = ref<ExternalLoginDto[]>([])
async function loadExternalLogins() {
  try {
    const res = await fetch('/api/account/external-logins')
    if (res.ok) externalLogins.value = await res.json()
  } catch { /* ignore — login page works without external buttons */ }
}
function startExternalLogin(loginProviderId: string) {
  const returnUrl = new URLSearchParams(window.location.search).get('returnUrl') ?? '/'
  const target = `/api/account/external-login/${loginProviderId}/start?returnUrl=${encodeURIComponent(returnUrl)}`
  window.location.href = target
}
loadExternalLogins()

// External IdP errors ride back on /login via ?error=<code> — the backend
// finish-endpoint redirects there when something rejects (unknown subject,
// email conflict, script failure, etc.). Translate the code into a friendly
// message and show it in the same banner as the regular form errors.
const idpErrorMessages: Record<string, string> = {
  'Idp.NotEnabled': t('auth.idp.notEnabled', {}, 'This identity provider is not available.'),
  'Idp.InvalidToken': t('auth.idp.invalidToken', {}, 'The identity provider did not return a valid response.'),
  'Idp.Unlinked': t('auth.idp.unlinked', {}, 'This external identity has been disconnected. Contact your administrator.'),
  'Idp.LinkedToOtherUser': t('auth.idp.linkedToOther', {}, 'This identity is already linked to a different Cocoar.Auth account.'),
  'Idp.UserMissing': t('auth.idp.userMissing', {}, 'The linked user no longer exists. Please contact your administrator.'),
  'Idp.EmailNotAllowed': t('auth.idp.emailNotAllowed', {}, 'Your email domain is not allowed for this provider.'),
  'Idp.EmailRequired': t('auth.idp.emailRequired', {}, 'The identity provider did not return an email. Cannot create a new account.'),
  'Idp.EmailConflict': t('auth.idp.emailConflict', {}, 'A Cocoar.Auth account with this email already exists. Please contact your administrator.'),
  'Idp.NoUserAndAutoCreateOff': t('auth.idp.noUser', {}, 'No Cocoar.Auth account is linked to this identity and auto-creation is disabled.'),
  'Idp.JitCreationFailed': t('auth.idp.jitFailed', {}, 'Could not create a new user account.'),
  'Idp.UserUpdateFailed': t('auth.idp.updateFailed', {}, 'Failed to update the user record from the identity provider.'),
  'oidc:Correlation failed.': t('auth.idp.correlationFailed', {}, 'Login session expired. Please try again.'),
}

const rawError = route.query.error as string | undefined
if (rawError) {
  error.value = idpErrorMessages[rawError]
    ?? t('auth.idp.genericError', { code: rawError }, 'Login via identity provider failed ({code}).')
}

// Flow steps
const step = ref<'credentials' | 'mfa-choice' | 'totp' | 'email-otp' | 'magic-link' | 'secure-setup'>('credentials')
const mfaMethods = ref<string[]>([])
const emailOtpSent = ref(false)
const magicLinkEmail = ref('')
const magicLinkSent = ref(false)

// Grace period state (populated from login response when RequiresSecureSetup)
const secureSetupInGrace = ref(false)
const secureSetupDueAt = ref<string | null>(null)

const isPasswordless = () => appConfig.config.AuthenticationMinimumLevel >= 2

async function handleLogin() {
  if (!userName.value.trim() || !password.value || submitting.value) return
  submitting.value = true
  error.value = ''

  try {
    const result = await authStore.login(userName.value.trim(), password.value, rememberMe.value)
    if (result?.RequiresSecureSetup) {
      secureSetupInGrace.value = result.GracePeriod === true
      secureSetupDueAt.value = result.SecureSetupDueAt ?? null
      step.value = 'secure-setup'
    } else if (result?.RequiresMfa) {
      mfaMethods.value = result.MfaMethods ?? []

      if (mfaMethods.value.length === 1 && mfaMethods.value[0] === 'totp') {
        step.value = 'totp'
      } else if (mfaMethods.value.length === 1 && mfaMethods.value[0] === 'email') {
        step.value = 'email-otp'
        await sendEmailOtp()
      } else if (mfaMethods.value.length > 1) {
        step.value = 'mfa-choice'
      } else {
        step.value = 'totp'
      }
    } else {
      finishLogin()
    }
  } catch (e) {
    if (e instanceof HttpClientError) {
      error.value = e.status === 401
        ? t('auth.login.invalidCredentials', {}, 'Invalid username or password.')
        : e.status === 403
          ? t('auth.login.passwordDisabled', {}, 'Password login is disabled.')
          : t('auth.login.error', { detail: e.statusText }, 'Error: {detail}')
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
  } finally {
    submitting.value = false
  }
}

async function chooseMfaMethod(method: string) {
  error.value = ''
  if (method === 'totp') {
    step.value = 'totp'
  } else if (method === 'email') {
    step.value = 'email-otp'
    await sendEmailOtp()
  }
}

async function sendEmailOtp() {
  error.value = ''
  emailOtpSent.value = false
  try {
    await authStore.requestEmailOtp()
    emailOtpSent.value = true
  } catch (e) {
    if (e instanceof HttpClientError) {
      error.value = e.body?.detail ?? t('auth.mfa.sendError', {}, 'Error sending code.')
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
  }
}

async function handleMfaLogin() {
  if (!totpCode.value.trim() || submitting.value) return
  submitting.value = true
  error.value = ''

  try {
    await authStore.mfaLogin(totpCode.value.replace(/[\s-]/g, ''), rememberMe.value)
    finishLogin()
  } catch (e) {
    if (e instanceof HttpClientError) {
      error.value = t('auth.mfa.invalidCode', {}, 'Invalid code. Please try again.')
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
  } finally {
    submitting.value = false
  }
}

async function handleEmailOtpLogin() {
  if (!emailOtpCode.value.trim() || submitting.value) return
  submitting.value = true
  error.value = ''

  try {
    await authStore.emailOtpLogin(emailOtpCode.value.replace(/[\s-]/g, ''), rememberMe.value)
    finishLogin()
  } catch (e) {
    if (e instanceof HttpClientError) {
      error.value = e.body?.detail ?? t('auth.mfa.invalidCode', {}, 'Invalid code. Please try again.')
    } else {
      error.value = t('common.connectionError', {}, 'Connection to server failed.')
    }
  } finally {
    submitting.value = false
  }
}

async function handleMagicLinkRequest() {
  if (!magicLinkEmail.value.trim() || submitting.value) return
  submitting.value = true
  error.value = ''

  try {
    await authStore.requestMagicLink(magicLinkEmail.value.trim())
    magicLinkSent.value = true
  } catch {
    error.value = t('common.connectionError', {}, 'Connection to server failed.')
  } finally {
    submitting.value = false
  }
}

function backToCredentials() {
  step.value = 'credentials'
  totpCode.value = ''
  emailOtpCode.value = ''
  emailOtpSent.value = false
  magicLinkSent.value = false
  magicLinkEmail.value = ''
  error.value = ''
}

function backToMfaChoice() {
  step.value = 'mfa-choice'
  totpCode.value = ''
  emailOtpCode.value = ''
  emailOtpSent.value = false
  error.value = ''
}

async function onSecureSetupComplete() {
  await authStore.fetchMe()
  finishLogin()
}

async function onSecureSetupPostpone() {
  // User is authenticated (cookie set during password login) but chose to delay 2FA setup.
  // Only offered while the grace period is still active.
  await authStore.fetchMe()
  finishLogin()
}

async function onSecureSetupLogout() {
  await authStore.logout()
  step.value = 'credentials'
}

// ── Passkey Login ──
const passkeyHttp = useHttpClient('/api/account/passkey')
const passkeyLoading = ref(false)

async function handlePasskeyLogin() {
  passkeyLoading.value = true
  error.value = ''
  try {
    const serverOptions = await passkeyHttp.addPath('login-options').post<any>({})

    const publicKey: PublicKeyCredentialRequestOptions = {
      challenge: base64UrlToBuffer(serverOptions.challenge),
      timeout: serverOptions.timeout,
      rpId: serverOptions.rpId,
      userVerification: serverOptions.userVerification,
      allowCredentials: (serverOptions.allowCredentials ?? []).map((c: any) => ({
        ...c,
        id: base64UrlToBuffer(c.id),
      })),
    }

    const credential = await navigator.credentials.get({ publicKey }) as PublicKeyCredential
    if (!credential) throw new Error('Cancelled')

    const response = credential.response as AuthenticatorAssertionResponse

    const assertion = {
      id: credential.id,
      rawId: bufferToBase64Url(credential.rawId),
      type: credential.type,
      response: {
        authenticatorData: bufferToBase64Url(response.authenticatorData),
        clientDataJSON: bufferToBase64Url(response.clientDataJSON),
        signature: bufferToBase64Url(response.signature),
        userHandle: response.userHandle ? bufferToBase64Url(response.userHandle) : null,
      },
    }

    await passkeyHttp.addPath('login').post(assertion, { params: { rememberMe: rememberMe.value } })
    await authStore.fetchMe()
    finishLogin()
  } catch (e: any) {
    if (e.name === 'NotAllowedError') {
      // User cancelled
    } else if (e instanceof HttpClientError) {
      error.value = t('auth.login.passkeyFailed', {}, 'Passkey login failed.')
    } else {
      error.value = e.message || t('auth.login.passkeyFailed', {}, 'Passkey login failed.')
    }
  } finally {
    passkeyLoading.value = false
  }
}

function base64UrlToBuffer(base64url: string): ArrayBuffer {
  const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/')
  const padded = base64.padEnd(base64.length + (4 - base64.length % 4) % 4, '=')
  const binary = atob(padded)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i)
  return bytes.buffer
}

function bufferToBase64Url(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-surface-50 p-4 relative">
    <button
      class="absolute top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>
    <div class="w-full max-w-sm">
      <!-- Logo + Title -->
      <div class="mb-8 text-center">
        <img src="/td-logo.svg" alt="Cocoar.Auth" class="mx-auto mb-1 h-16 w-auto" />
        <h1 class="text-2xl font-bold tracking-tight text-surface-800">
          Cocoar<span class="text-[#525e76]">.Auth</span>
        </h1>
        <p class="mt-2 text-sm text-surface-500">
          <template v-if="step === 'credentials'">{{ t('auth.login.subtitle', {}, 'Sign in to continue.') }}</template>
          <template v-else-if="step === 'mfa-choice'">{{ t('auth.mfa.chooseMethod', {}, 'Choose a verification method.') }}</template>
          <template v-else-if="step === 'totp'">{{ t('auth.mfa.totpSubtitle', {}, 'Enter the code from your authenticator app.') }}</template>
          <template v-else-if="step === 'email-otp'">{{ t('auth.mfa.emailOtpSubtitle', {}, 'Enter the code from your email.') }}</template>
          <template v-else-if="step === 'magic-link'">{{ t('auth.mfa.magicLinkSubtitle', {}, 'Receive a login link via email.') }}</template>
          <template v-else-if="step === 'secure-setup'">{{ t('auth.secureSetup.subtitle', {}, 'Secure your account before continuing.') }}</template>
        </p>
      </div>

      <!-- Secure Setup Modal (inline, replaces login card) -->
      <SecureSetupModal
        v-if="step === 'secure-setup'"
        :in-grace="secureSetupInGrace"
        :due-at="secureSetupDueAt"
        @complete="onSecureSetupComplete"
        @postpone="onSecureSetupPostpone"
        @logout="onSecureSetupLogout"
      />

      <CoarCard v-else elevated>
        <!-- Step 1: Username + Password (or passwordless alternatives) -->
        <form v-if="step === 'credentials'" class="space-y-4" @submit.prevent="handleLogin">
          <!-- Password login (hidden at Level 2) -->
          <template v-if="!isPasswordless()">
            <CoarFormField :label="t('auth.login.username', {}, 'Username')">
              <CoarTextInput
                v-model="userName"
                :placeholder="t('auth.login.username', {}, 'Username')"
                autocomplete="username"
                required
              />
            </CoarFormField>

            <CoarFormField :label="t('auth.login.password', {}, 'Password')">
              <CoarPasswordInput
                v-model="password"
                :placeholder="t('auth.login.password', {}, 'Password')"
                autocomplete="current-password"
                required
              />
            </CoarFormField>

            <CoarCheckbox v-model="rememberMe" :label="t('auth.login.rememberMe', {}, 'Stay signed in')" />

            <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>

            <CoarButton
              type="submit"
              :disabled="!userName.trim() || !password"
              :loading="submitting"
              full-width
            >
              {{ t('auth.login.submit', {}, 'Sign in') }}
            </CoarButton>
          </template>

          <!-- Passwordless notice (Level 2) -->
          <CoarNote v-if="isPasswordless()" variant="info">
            {{ t('auth.login.passwordlessMode', {}, 'This application uses passwordless login.') }}
          </CoarNote>

          <CoarNote v-if="isPasswordless() && error" variant="error">{{ error }}</CoarNote>

          <!-- Divider -->
          <div class="flex items-center gap-3 text-surface-400 text-xs">
            <div class="flex-1 border-t border-surface-200"></div>
            <template v-if="!isPasswordless()">{{ t('common.or', {}, 'or') }}</template>
            <div class="flex-1 border-t border-surface-200"></div>
          </div>

          <!-- Passkey login (always available) -->
          <CoarButton
            type="button"
            variant="secondary"
            :loading="passkeyLoading"
            full-width
            @click="handlePasskeyLogin"
          >
            {{ t('auth.login.passkeyLogin', {}, 'Sign in with Passkey') }}
          </CoarButton>

          <!-- Magic Link Self-Service -->
          <CoarButton
            v-if="appConfig.config.MagicLinkSelfService"
            type="button"
            variant="secondary"
            full-width
            @click="step = 'magic-link'"
          >
            {{ t('auth.login.magicLinkButton', {}, 'Login link via email') }}
          </CoarButton>

          <!-- External identity providers (OIDC) -->
          <CoarButton
            v-for="idp in externalLogins"
            :key="idp.Id"
            type="button"
            variant="secondary"
            full-width
            :style="idp.ButtonColorHex ? { borderColor: idp.ButtonColorHex, color: idp.ButtonColorHex } : {}"
            @click="startExternalLogin(idp.Id)"
          >
            {{ t('auth.login.externalPrefix', {}, 'Sign in with') }} {{ idp.DisplayName }}
          </CoarButton>

          <RouterLink v-if="!isPasswordless()" to="/forgot-password" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.login.forgotPassword', {}, 'Forgot password?') }}
          </RouterLink>
        </form>

        <!-- Step: MFA Choice -->
        <div v-else-if="step === 'mfa-choice'" class="space-y-4">
          <CoarButton v-if="mfaMethods.includes('totp')" full-width @click="chooseMfaMethod('totp')">
            {{ t('auth.mfa.authenticatorApp', {}, 'Authenticator App') }}
          </CoarButton>

          <CoarButton v-if="mfaMethods.includes('email')" full-width variant="secondary" @click="chooseMfaMethod('email')">
            {{ t('auth.mfa.emailCode', {}, 'Code via Email') }}
          </CoarButton>

          <div class="text-center">
            <button type="button" class="text-sm text-surface-500 hover:text-surface-700 hover:underline" @click="backToCredentials">
              {{ t('auth.mfa.backToLogin', {}, 'Back to login') }}
            </button>
          </div>
        </div>

        <!-- Step: TOTP Code -->
        <form v-else-if="step === 'totp'" class="space-y-4" @submit.prevent="handleMfaLogin">
          <CoarFormField :label="t('auth.mfa.authenticatorCode', {}, 'Authenticator Code')">
            <CoarTextInput v-model="totpCode" placeholder="000 000" autocomplete="one-time-code" required />
          </CoarFormField>
          <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
          <CoarButton type="submit" :disabled="!totpCode.trim()" :loading="submitting" full-width>
            {{ t('common.confirm', {}, 'Confirm') }}
          </CoarButton>
          <div class="text-center">
            <button type="button" class="text-sm text-surface-500 hover:text-surface-700 hover:underline"
              @click="mfaMethods.length > 1 ? backToMfaChoice() : backToCredentials()">
              {{ mfaMethods.length > 1 ? t('auth.mfa.otherMethod', {}, 'Choose other method') : t('auth.mfa.backToLogin', {}, 'Back to login') }}
            </button>
          </div>
        </form>

        <!-- Step: Email OTP -->
        <form v-else-if="step === 'email-otp'" class="space-y-4" @submit.prevent="handleEmailOtpLogin">
          <CoarNote v-if="emailOtpSent" variant="success">
            {{ t('auth.emailOtp.codeSent', {}, 'A code was sent to your email address.') }}
          </CoarNote>
          <CoarFormField :label="t('auth.emailOtp.label', {}, 'Email Code')">
            <CoarTextInput v-model="emailOtpCode" placeholder="000000" autocomplete="one-time-code" required />
          </CoarFormField>
          <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
          <CoarButton type="submit" :disabled="!emailOtpCode.trim()" :loading="submitting" full-width>
            {{ t('common.confirm', {}, 'Confirm') }}
          </CoarButton>
          <div class="flex justify-between text-sm">
            <button type="button" class="text-surface-500 hover:text-surface-700 hover:underline" @click="sendEmailOtp">
              {{ t('auth.emailOtp.resendCode', {}, 'Resend code') }}
            </button>
            <button type="button" class="text-surface-500 hover:text-surface-700 hover:underline"
              @click="mfaMethods.length > 1 ? backToMfaChoice() : backToCredentials()">
              {{ mfaMethods.length > 1 ? t('auth.emailOtp.otherMethod', {}, 'Other method') : t('auth.emailOtp.back', {}, 'Back') }}
            </button>
          </div>
        </form>

        <!-- Step: Magic Link -->
        <div v-else-if="step === 'magic-link'" class="space-y-4">
          <template v-if="!magicLinkSent">
            <CoarFormField :label="t('auth.magicLink.emailLabel', {}, 'Email Address')">
              <CoarTextInput v-model="magicLinkEmail" :placeholder="t('auth.magicLink.emailPlaceholder', {}, 'email@example.com')"
                type="email" required @keydown.enter="handleMagicLinkRequest" />
            </CoarFormField>
            <CoarNote v-if="error" variant="error">{{ error }}</CoarNote>
            <CoarButton :disabled="!magicLinkEmail.trim()" :loading="submitting" full-width @click="handleMagicLinkRequest">
              {{ t('auth.magicLink.sendLink', {}, 'Send link') }}
            </CoarButton>
          </template>
          <template v-else>
            <CoarNote variant="success">
              {{ t('auth.magicLink.sent', {}, 'If an account exists with this email, a login link was sent. Please check your inbox.') }}
            </CoarNote>
          </template>
          <div class="text-center">
            <button type="button" class="text-sm text-surface-500 hover:text-surface-700 hover:underline" @click="backToCredentials">
              {{ t('auth.mfa.backToLogin', {}, 'Back to login') }}
            </button>
          </div>
        </div>
      </CoarCard>
    </div>
  </div>
</template>
