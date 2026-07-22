<script setup lang="ts">
import { ref, computed, onMounted, provide } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth.store'
import { useAppConfigStore } from '@/stores/appconfig.store'
import { useHttpClient, HttpClientError } from '@/composables/useHttpClient'
import { useLoginRedirect } from '@/composables/useLoginRedirect'
import { useI18n, useLocalization } from '@cocoar/vue-localization'
import {
  CoarCard,
  CoarButton,
  CoarTextInput,
  CoarPasswordInput,
  CoarFormField,
  CoarCheckbox,
  CoarNote,
  CoarOtpInput,
} from '@cocoar/vue-ui'
import SecureSetupModal from './SecureSetupModal.vue'
import {
  CoarPageRenderer,
  normalizePageSchema,
  type ActionHandler,
  type ActionValues,
  type PageNode,
} from '@cocoar/vue-page-builder'
import { createAuthPageConfig } from '@/page-builder/authPageConfig'
import {
  LOGIN_PAGE_RUNTIME_KEY,
  type ExternalLoginDto,
} from '@/page-builder/loginPageRuntime'

const { t, language } = useI18n()
const localization = useLocalization()!
const appConfig = useAppConfigStore()
const branding = computed(() => appConfig.config.Branding)

async function toggleLanguage() {
  const next = language.value === 'de' ? 'en' : 'de'
  await localization.setLanguage(next)
  localStorage.setItem('language', next)
}

const route = useRoute()
const router = useRouter()
const authStore = useAuthStore()

// Post-login continuation — reads ?redirect= (same-origin-guarded) and
// finishes every successful login through the shared redirect logic.
const { redirectTarget, finishLogin } = useLoginRedirect()

// HttpClientError.body is `unknown` by design — narrow it here. Most
// API errors are ProblemDetails-shaped, so we look for `.detail`.
function errorDetail(e: HttpClientError): string | undefined {
  const b = e.body
  if (b && typeof b === 'object' && 'detail' in b && typeof b.detail === 'string') {
    return b.detail
  }
  return undefined
}

// Form state
const userName = ref('')
const password = ref('')
const rememberMe = ref(false)
const totpCode = ref('')
const emailOtpCode = ref('')
const submitting = ref(false)
const error = ref('')

// External auth (OIDC + SAML) — fetched anonymously, enabled providers only.
// Kind decides the entry-point URL (OIDC challenge vs SAML SP-initiated).
const externalLogins = ref<ExternalLoginDto[]>([])
async function loadExternalLogins() {
  try {
    const res = await fetch('/api/account/external-logins')
    if (res.ok) externalLogins.value = await res.json()
  } catch { /* ignore — login page works without external buttons */ }
}

// Self-registration toggle — fetched anonymously. Adds a "Register"
// link to the login screen iff the realm has self-reg opted in.
const selfRegistrationEnabled = ref(false)
async function loadSelfRegistrationInfo() {
  try {
    const res = await fetch('/api/account/self-registration-info')
    if (!res.ok) return
    const info = await res.json()
    selfRegistrationEnabled.value = !!info?.Enabled
  } catch { /* ignore — login page works without register link */ }
}
loadSelfRegistrationInfo()

function startExternalLogin(idp: ExternalLoginDto) {
  // The pending continuation rides ?redirect= (set by the cookie handler /
  // router guard) — redirectTarget already applies the same-origin guard.
  // The backend start endpoints stash it and the finish endpoints redirect
  // there after the external round trip, so a /connect/authorize target
  // resumes the client app's OIDC flow.
  const returnUrl = redirectTarget.value
  // SAML is SP-initiated via its own slug-based route; OIDC goes through the
  // challenge start endpoint keyed by provider id.
  const target = idp.Kind === 'Saml'
    ? `/saml/${encodeURIComponent(idp.Slug)}/login?returnUrl=${encodeURIComponent(returnUrl)}`
    : `/api/account/external-login/${idp.Id}/start?returnUrl=${encodeURIComponent(returnUrl)}`
  window.location.href = target
}
provide(LOGIN_PAGE_RUNTIME_KEY, { branding, externalLogins, startExternalLogin })
loadExternalLogins()

// External IdP errors ride back on /login via ?error=<code> — the backend
// finish-endpoint redirects there when something rejects (unknown subject,
// email conflict, script failure, etc.). Translate the code into a friendly
// message and show it in the same banner as the regular form errors.
const idpErrorMessages: Record<string, string> = {
  'Idp.NotEnabled': t('auth.idp.notEnabled', {}, 'This identity provider is not available.'),
  'Idp.InvalidToken': t('auth.idp.invalidToken', {}, 'The identity provider did not return a valid response.'),
  'Idp.Unlinked': t('auth.idp.unlinked', {}, 'This external identity has been disconnected. Contact your administrator.'),
  'Idp.LinkedToOtherUser': t('auth.idp.linkedToOther', {}, 'This identity is already linked to a different Modgud account.'),
  'Idp.UserMissing': t('auth.idp.userMissing', {}, 'The linked user no longer exists. Please contact your administrator.'),
  'Idp.EmailNotAllowed': t('auth.idp.emailNotAllowed', {}, 'Your email domain is not allowed for this provider.'),
  'Idp.EmailRequired': t('auth.idp.emailRequired', {}, 'The identity provider did not return an email. Cannot create a new account.'),
  'Idp.EmailConflict': t('auth.idp.emailConflict', {}, 'A Modgud account with this email already exists. Please contact your administrator.'),
  'Idp.NoUserAndAutoCreateOff': t('auth.idp.noUser', {}, 'No Modgud account is linked to this identity and auto-creation is disabled.'),
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

const loginPageConfig = createAuthPageConfig('login')
const customLoginSchema = ref<PageNode | null>(null)
const loginPageReady = ref(false)

onMounted(async () => {
  try {
    await appConfig.loadForLogin(redirectTarget.value)
    if (!appConfig.config.Features.PageBuilder || route.query.safemode === '1') return
    const stored = appConfig.config.Pages.login
    if (!stored) return
    const normalized = normalizePageSchema(JSON.parse(stored), { elements: loginPageConfig.elements })
    customLoginSchema.value = normalized.schema
  } catch {
    // A broken or unreachable customization must never make authentication
    // unavailable. The fixed login below remains the emergency-safe fallback.
    customLoginSchema.value = null
  } finally {
    loginPageReady.value = true
  }
})

function loginErrorMessage(e: unknown): string {
  if (e instanceof HttpClientError) {
    return e.status === 401
      ? t('auth.login.invalidCredentials', {}, 'Invalid username or password.')
      : e.status === 403
        ? t('auth.login.passwordDisabled', {}, 'Password login is disabled.')
        : t('auth.login.error', { detail: e.statusText }, 'Error: {detail}')
  }
  return t('common.connectionError', {}, 'Connection to server failed.')
}

async function performCredentialLogin(name: string, secret: string, remember: boolean) {
  try {
    const result = await authStore.login(name.trim(), secret, remember)
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
    throw new Error(loginErrorMessage(e))
  }
}

async function handleLogin() {
  if (!userName.value.trim() || !password.value || submitting.value) return
  submitting.value = true
  error.value = ''

  try {
    await performCredentialLogin(userName.value, password.value, rememberMe.value)
  } catch (e) {
    error.value = e instanceof Error ? e.message : loginErrorMessage(e)
  } finally {
    submitting.value = false
  }
}

function requiredString(values: ActionValues, name: string): string {
  const value = values[name]
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error(t('auth.login.missingFields', {}, 'Please complete all required fields.'))
  }
  return value
}

const customLoginActions: Record<string, ActionHandler> = {
  'auth:login': async (values) => {
    error.value = ''
    userName.value = requiredString(values, 'username')
    password.value = requiredString(values, 'password')
    rememberMe.value = values.rememberMe === true
    await performCredentialLogin(userName.value, password.value, rememberMe.value)
  },
  'auth:passkey': async (values) => {
    error.value = ''
    rememberMe.value = values.rememberMe === true
    await handlePasskeyLogin(true)
  },
  'auth:magic-link': () => {
    if (!appConfig.config.MagicLinkSelfService) {
      throw new Error(t('auth.magicLink.notAvailable', {}, 'Magic-link login is not available.'))
    }
    step.value = 'magic-link'
  },
  'auth:forgot-password': () => router.push({ path: '/forgot-password', query: { redirect: route.query.redirect } }),
  'auth:register': () => {
    if (!selfRegistrationEnabled.value) {
      throw new Error(t('auth.registration.notAvailable', {}, 'Registration is not available.'))
    }
    return router.push({ path: '/register', query: { redirect: route.query.redirect } })
  },
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
      error.value = errorDetail(e) ?? t('auth.mfa.sendError', {}, 'Error sending code.')
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
      error.value = errorDetail(e) ?? t('auth.mfa.invalidCode', {}, 'Invalid code. Please try again.')
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
    // Pass the pending continuation along — it survives the e-mail round
    // trip as ?redirect= on the emailed /magic-login URL.
    await authStore.requestMagicLink(magicLinkEmail.value.trim(), redirectTarget.value)
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

async function handlePasskeyLogin(reportToRenderer = false) {
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

    await passkeyHttp
      .addPath('login')
      .setQueryParameter('rememberMe', String(rememberMe.value))
      .post(assertion)
    await authStore.fetchMe()
    finishLogin()
  } catch (e: any) {
    if (e.name === 'NotAllowedError') {
      // User cancelled
    } else if (e instanceof HttpClientError) {
      const message = t('auth.login.passkeyFailed', {}, 'Passkey login failed.')
      if (reportToRenderer) throw new Error(message)
      error.value = message
    } else {
      const message = e.message || t('auth.login.passkeyFailed', {}, 'Passkey login failed.')
      if (reportToRenderer) throw new Error(message)
      error.value = message
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
  <div class="min-h-screen bg-surface-50 relative">
    <button
      class="absolute z-10 top-4 right-4 text-xs text-surface-400 hover:text-surface-600 transition"
      @click="toggleLanguage"
    >
      {{ language === 'de' ? 'EN' : 'DE' }}
    </button>

    <div v-if="step === 'credentials' && !loginPageReady" class="flex min-h-screen items-center justify-center text-sm text-surface-400">
      {{ t('common.loading', {}, 'Loading…') }}
    </div>

    <template v-else-if="step === 'credentials' && customLoginSchema && !isPasswordless()">
      <CoarNote v-if="error" variant="error" class="custom-login-error">{{ error }}</CoarNote>
      <CoarPageRenderer
        :schema="customLoginSchema"
        :config="loginPageConfig"
        :actions="customLoginActions"
      />
    </template>

    <div v-else class="flex min-h-screen items-center justify-center p-4">
      <div class="w-full max-w-sm">
      <!-- Logo + Title -->
      <div class="mb-8 text-center">
        <img :src="branding.LogoUrl ?? '/idp-logo.svg'" :alt="branding.ProductName ?? 'Modgud'" class="mx-auto mb-1 h-16 w-auto" />
        <h1 v-if="branding.ProductName" class="text-2xl font-bold tracking-tight text-surface-800">
          {{ branding.ProductName }}
        </h1>
        <h1 v-else class="text-2xl font-bold tracking-tight text-surface-800">
          Modgud
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
            @click="handlePasskeyLogin()"
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

          <!-- External identity providers (OIDC + SAML) -->
          <CoarButton
            v-for="idp in externalLogins"
            :key="idp.Id"
            type="button"
            variant="secondary"
            full-width
            :style="idp.ButtonColorHex ? { borderColor: idp.ButtonColorHex, color: idp.ButtonColorHex } : {}"
            @click="startExternalLogin(idp)"
          >
            {{ t('auth.login.externalPrefix', {}, 'Sign in with') }} {{ idp.DisplayName }}
          </CoarButton>

          <!-- Detours forward ?redirect= so the pending continuation (e.g. a
               client app's /connect/authorize flow) survives the side trip. -->
          <RouterLink v-if="!isPasswordless()" :to="{ path: '/forgot-password', query: { redirect: route.query.redirect } }" class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.login.forgotPassword', {}, 'Forgot password?') }}
          </RouterLink>

          <RouterLink v-if="selfRegistrationEnabled" :to="{ path: '/register', query: { redirect: route.query.redirect } }"
            class="block text-center text-sm text-surface-500 hover:text-surface-700 hover:underline">
            {{ t('auth.login.registerLink', {}, 'No account yet? Register →') }}
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
            <CoarOtpInput v-model="totpCode" type="numeric" :length="6" auto-focus required />
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
            <CoarOtpInput v-model="emailOtpCode" type="numeric" :length="6" auto-focus required />
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
  </div>
</template>

<style scoped>
.custom-login-error {
  position: fixed;
  z-index: 5;
  top: 3.25rem;
  left: 50%;
  width: min(25rem, calc(100vw - 2rem));
  transform: translateX(-50%);
}
</style>
